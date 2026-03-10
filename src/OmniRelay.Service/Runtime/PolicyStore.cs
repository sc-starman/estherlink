using Microsoft.Data.Sqlite;
using OmniRelay.Core.Policy;

namespace OmniRelay.Service.Runtime;

public sealed class PolicyStore
{
    private readonly FileLogWriter _log;

    public PolicyStore(FileLogWriter log)
    {
        _log = log;
        EnsureSchema();
    }

    public PolicyStoreSnapshot Load()
    {
        using var connection = OpenConnection();
        var whitelist = GetEntries(connection, PolicyListTypes.Whitelist);
        var blacklist = GetEntries(connection, PolicyListTypes.Blacklist);
        var revision = GetMetaLong(connection, "revision");
        var updatedAtUtc = GetMetaDateTimeOffset(connection, "updated_utc");

        return new PolicyStoreSnapshot(whitelist, blacklist, revision, updatedAtUtc);
    }

    public PolicyListSnapshot GetList(string listType)
    {
        var normalizedListType = PolicyListTypes.Normalize(listType);
        using var connection = OpenConnection();
        var entries = GetEntries(connection, normalizedListType);
        var revision = GetMetaLong(connection, "revision");
        var updatedAtUtc = GetMetaDateTimeOffset(connection, "updated_utc");
        return new PolicyListSnapshot(normalizedListType, entries, entries.Count, revision, updatedAtUtc);
    }

    public bool ImportLegacyWhitelistIfEmpty(IReadOnlyList<string> legacyEntries)
    {
        if (legacyEntries.Count == 0)
        {
            return false;
        }

        using var connection = OpenConnection();
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM policy_entries WHERE list_type = $listType;";
        countCmd.Parameters.AddWithValue("$listType", PolicyListTypes.Whitelist);
        var current = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        if (current > 0)
        {
            return false;
        }

        var result = ApplyUpdate(connection, PolicyListTypes.Whitelist, PolicyUpdateModes.Replace, legacyEntries);
        _log.Info($"Imported {result.AppliedCount} legacy whitelist entries into policy.db.");
        return result.AppliedCount > 0;
    }

    public PolicyCommitResult ApplyUpdate(string listType, string mode, IReadOnlyList<string> entries)
    {
        using var connection = OpenConnection();
        return ApplyUpdate(connection, listType, mode, entries);
    }

    private PolicyCommitResult ApplyUpdate(SqliteConnection connection, string listType, string mode, IReadOnlyList<string> entries)
    {
        var normalizedListType = PolicyListTypes.Normalize(listType);
        var normalizedMode = PolicyUpdateModes.Normalize(mode);

        var invalidCount = 0;
        var duplicateDroppedCount = 0;
        var unique = new Dictionary<string, ParsedPolicyEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in entries)
        {
            if (!TryParsePolicyEntry(raw, out var parsed, out var hasInput))
            {
                if (hasInput)
                {
                    invalidCount++;
                }

                continue;
            }

            if (!unique.TryAdd(parsed.Canonical, parsed))
            {
                duplicateDroppedCount++;
            }
        }

        using var tx = connection.BeginTransaction();

        var existing = normalizedMode == PolicyUpdateModes.Merge
            ? GetExistingCanonicalSet(connection, tx, normalizedListType)
            : [];

        if (normalizedMode == PolicyUpdateModes.Replace)
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM policy_entries WHERE list_type = $listType;";
            deleteCmd.Parameters.AddWithValue("$listType", normalizedListType);
            deleteCmd.ExecuteNonQuery();
        }

        var toInsert = new List<ParsedPolicyEntry>(unique.Count);
        foreach (var parsed in unique.Values)
        {
            if (normalizedMode == PolicyUpdateModes.Merge && existing.Contains(parsed.Canonical))
            {
                duplicateDroppedCount++;
                continue;
            }

            toInsert.Add(parsed);
        }

        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO policy_entries (list_type, canonical, raw, family, kind, network, prefix_len)
                VALUES ($list_type, $canonical, $raw, $family, $kind, $network, $prefix_len);
                """;

            insertCmd.Parameters.Add("$list_type", SqliteType.Text);
            insertCmd.Parameters.Add("$canonical", SqliteType.Text);
            insertCmd.Parameters.Add("$raw", SqliteType.Text);
            insertCmd.Parameters.Add("$family", SqliteType.Integer);
            insertCmd.Parameters.Add("$kind", SqliteType.Integer);
            insertCmd.Parameters.Add("$network", SqliteType.Blob);
            insertCmd.Parameters.Add("$prefix_len", SqliteType.Integer);

            foreach (var parsed in toInsert)
            {
                insertCmd.Parameters["$list_type"].Value = normalizedListType;
                insertCmd.Parameters["$canonical"].Value = parsed.Canonical;
                insertCmd.Parameters["$raw"].Value = parsed.Raw;
                insertCmd.Parameters["$family"].Value = parsed.Family;
                insertCmd.Parameters["$kind"].Value = parsed.Kind;
                insertCmd.Parameters["$network"].Value = parsed.Network;
                insertCmd.Parameters["$prefix_len"].Value = parsed.PrefixLength;
                insertCmd.ExecuteNonQuery();
            }
        }

        var updatedAt = DateTimeOffset.UtcNow;
        var revision = GetMetaLong(connection, "revision", tx) + 1;
        SetMeta(connection, "revision", revision.ToString(), tx);
        SetMeta(connection, "updated_utc", updatedAt.ToString("O"), tx);

        var count = GetListCount(connection, normalizedListType, tx);
        SetMeta(connection, "whitelist_count", GetListCount(connection, PolicyListTypes.Whitelist, tx).ToString(), tx);
        SetMeta(connection, "blacklist_count", GetListCount(connection, PolicyListTypes.Blacklist, tx).ToString(), tx);

        tx.Commit();

        return new PolicyCommitResult(
            normalizedListType,
            normalizedMode,
            toInsert.Count,
            duplicateDroppedCount,
            invalidCount,
            count,
            revision,
            updatedAt);
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS policy_entries (
              list_type TEXT NOT NULL,
              canonical TEXT NOT NULL,
              raw TEXT NOT NULL,
              family INTEGER NOT NULL,
              kind INTEGER NOT NULL,
              network BLOB NOT NULL,
              prefix_len INTEGER NOT NULL,
              PRIMARY KEY(list_type, canonical)
            );

            CREATE TABLE IF NOT EXISTS policy_meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        EnsureMetaDefault(connection, "revision", "0");
        EnsureMetaDefault(connection, "updated_utc", DateTimeOffset.MinValue.ToString("O"));
        EnsureMetaDefault(connection, "whitelist_count", "0");
        EnsureMetaDefault(connection, "blacklist_count", "0");
    }

    private static void EnsureMetaDefault(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO policy_meta (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static List<string> GetEntries(SqliteConnection connection, string listType)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT canonical
            FROM policy_entries
            WHERE list_type = $listType
            ORDER BY canonical;
            """;
        command.Parameters.AddWithValue("$listType", listType);
        using var reader = command.ExecuteReader();
        var entries = new List<string>();
        while (reader.Read())
        {
            entries.Add(reader.GetString(0));
        }

        return entries;
    }

    private static HashSet<string> GetExistingCanonicalSet(SqliteConnection connection, SqliteTransaction tx, string listType)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT canonical
            FROM policy_entries
            WHERE list_type = $listType;
            """;
        command.Parameters.AddWithValue("$listType", listType);
        using var reader = command.ExecuteReader();
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static int GetListCount(SqliteConnection connection, string listType, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM policy_entries WHERE list_type = $listType;";
        command.Parameters.AddWithValue("$listType", listType);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static long GetMetaLong(SqliteConnection connection, string key, SqliteTransaction? tx = null)
    {
        var raw = GetMeta(connection, key, tx);
        return long.TryParse(raw, out var value) ? value : 0L;
    }

    private static DateTimeOffset GetMetaDateTimeOffset(SqliteConnection connection, string key, SqliteTransaction? tx = null)
    {
        var raw = GetMeta(connection, key, tx);
        return DateTimeOffset.TryParse(raw, out var value) ? value : DateTimeOffset.MinValue;
    }

    private static string GetMeta(SqliteConnection connection, string key, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT value FROM policy_meta WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static void SetMeta(SqliteConnection connection, string key, string value, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO policy_meta (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static bool TryParsePolicyEntry(string? raw, out ParsedPolicyEntry parsed, out bool hasInput)
    {
        parsed = new ParsedPolicyEntry(string.Empty, string.Empty, 0, 0, [], 0);
        var value = (raw ?? string.Empty).Trim();
        hasInput = !string.IsNullOrWhiteSpace(value);
        if (!hasInput)
        {
            return false;
        }

        var commentIndex = value.IndexOf('#');
        if (commentIndex >= 0)
        {
            value = value[..commentIndex].Trim();
        }

        hasInput = !string.IsNullOrWhiteSpace(value);
        if (!hasInput)
        {
            return false;
        }

        if (!NetworkRule.TryParse(value, out var rule, out _) || rule is null)
        {
            return false;
        }

        var isHost = (rule.NetworkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && rule.PrefixLength == 32) ||
                     (rule.NetworkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && rule.PrefixLength == 128);
        var canonical = isHost ? rule.NetworkAddress.ToString() : $"{rule.NetworkAddress}/{rule.PrefixLength}";
        var family = rule.NetworkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 6;
        var kind = isHost ? 0 : 1;
        parsed = new ParsedPolicyEntry(canonical, value, family, kind, rule.NetworkAddress.GetAddressBytes(), rule.PrefixLength);
        return true;
    }

    private SqliteConnection OpenConnection()
    {
        ServicePaths.EnsureDirectories();
        var connection = new SqliteConnection($"Data Source={ServicePaths.PolicyDbPath};Cache=Shared");
        connection.Open();
        return connection;
    }

    private sealed record ParsedPolicyEntry(
        string Canonical,
        string Raw,
        int Family,
        int Kind,
        byte[] Network,
        int PrefixLength);
}

public sealed record PolicyStoreSnapshot(
    IReadOnlyList<string> WhitelistEntries,
    IReadOnlyList<string> BlacklistEntries,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record PolicyListSnapshot(
    string ListType,
    IReadOnlyList<string> Entries,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record PolicyCommitResult(
    string ListType,
    string Mode,
    int AppliedCount,
    int DuplicateDroppedCount,
    int InvalidCount,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);
