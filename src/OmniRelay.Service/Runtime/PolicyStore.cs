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

    public RelayPolicySetSnapshot LoadPolicySet(string relayId = "")
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        using var connection = OpenConnection();
        var lists = LoadRelayPolicyLists(connection, normalizedRelayId, includeEntries: true);
        var revision = GetMetaLong(connection, normalizedRelayId, "revision");
        var updatedAt = GetMetaDateTimeOffset(connection, normalizedRelayId, "updated_utc");
        var whitelistCount = lists.Count(x => string.Equals(PolicyListTypes.Normalize(x.ListType), PolicyListTypes.Whitelist, StringComparison.OrdinalIgnoreCase));
        var blacklistCount = lists.Count(x => string.Equals(PolicyListTypes.Normalize(x.ListType), PolicyListTypes.Blacklist, StringComparison.OrdinalIgnoreCase));
        return new RelayPolicySetSnapshot(
            normalizedRelayId,
            lists,
            revision,
            updatedAt,
            lists.Count,
            lists.Sum(x => x.EntryCount),
            whitelistCount,
            blacklistCount);
    }

    // Compatibility snapshot for legacy runtime/UI paths. It flattens list entities by type.
    public PolicyStoreSnapshot LoadLegacyFlattened(string relayId = "")
    {
        var snapshot = LoadPolicySet(relayId);
        var whitelist = snapshot.Lists
            .Where(x => string.Equals(PolicyListTypes.Normalize(x.ListType), PolicyListTypes.Whitelist, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Priority)
            .SelectMany(x => x.Entries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var blacklist = snapshot.Lists
            .Where(x => string.Equals(PolicyListTypes.Normalize(x.ListType), PolicyListTypes.Blacklist, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Priority)
            .SelectMany(x => x.Entries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PolicyStoreSnapshot(whitelist, blacklist, snapshot.Revision, snapshot.UpdatedAtUtc);
    }

    public PolicyListSnapshot GetList(string listType, string relayId = "")
    {
        var normalizedListType = PolicyListTypes.Normalize(listType);
        var snapshot = LoadPolicySet(relayId);
        var entries = snapshot.Lists
            .Where(x => string.Equals(PolicyListTypes.Normalize(x.ListType), normalizedListType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Priority)
            .SelectMany(x => x.Entries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PolicyListSnapshot(
            normalizedListType,
            entries,
            entries.Count,
            snapshot.Revision,
            snapshot.UpdatedAtUtc);
    }

    public PolicyStoreSnapshot Load(string relayId = "")
    {
        return LoadLegacyFlattened(relayId);
    }

    public IReadOnlyList<RelayPolicyListSummary> ListPolicyLists(string relayId = "")
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        using var connection = OpenConnection();
        return LoadRelayPolicyLists(connection, normalizedRelayId, includeEntries: false)
            .Select(x => new RelayPolicyListSummary(x.ListId, x.RelayId, x.Label, x.ListType, x.Priority, x.EntryCount))
            .ToList();
    }

    public RelayPolicyList? GetPolicyList(string listId, string relayId = "")
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedListId = (listId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedListId))
        {
            return null;
        }

        using var connection = OpenConnection();
        return LoadPolicyListById(connection, normalizedRelayId, normalizedListId, includeEntries: true);
    }

    public RelayPolicyListSummary CreatePolicyList(string relayId, string label, string listType, int? priority = null)
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedType = PolicyListTypes.Normalize(listType);
        var normalizedLabel = NormalizeLabel(label);
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        var priorities = GetRelayListPriorities(connection, tx, normalizedRelayId);
        var insertPriority = ResolveInsertPriority(priorities, priority);
        ShiftPrioritiesAtOrAfter(connection, tx, normalizedRelayId, insertPriority);

        var listId = Guid.NewGuid().ToString("N");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO policy_lists (list_id, relay_id, label, list_type, priority, created_utc, updated_utc)
                VALUES ($listId, $relayId, $label, $listType, $priority, $nowUtc, $nowUtc);
                """;
            var now = DateTimeOffset.UtcNow.ToString("O");
            insert.Parameters.AddWithValue("$listId", listId);
            insert.Parameters.AddWithValue("$relayId", normalizedRelayId);
            insert.Parameters.AddWithValue("$label", normalizedLabel);
            insert.Parameters.AddWithValue("$listType", normalizedType);
            insert.Parameters.AddWithValue("$priority", insertPriority);
            insert.Parameters.AddWithValue("$nowUtc", now);
            insert.ExecuteNonQuery();
        }

        TouchRelayMeta(connection, tx, normalizedRelayId);
        tx.Commit();
        return new RelayPolicyListSummary(listId, normalizedRelayId, normalizedLabel, normalizedType, insertPriority, 0);
    }

    public RelayPolicyListSummary UpdatePolicyListMeta(string relayId, string listId, string label, string listType)
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedListId = (listId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedListId))
        {
            throw new InvalidOperationException("Policy list id is required.");
        }

        var normalizedLabel = NormalizeLabel(label);
        var normalizedType = PolicyListTypes.Normalize(listType);
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        EnsureListOwnedByRelay(connection, tx, normalizedRelayId, normalizedListId);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE policy_lists
                SET label = $label,
                    list_type = $listType,
                    updated_utc = $updatedUtc
                WHERE relay_id = $relayId AND list_id = $listId;
                """;
            update.Parameters.AddWithValue("$label", normalizedLabel);
            update.Parameters.AddWithValue("$listType", normalizedType);
            update.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$relayId", normalizedRelayId);
            update.Parameters.AddWithValue("$listId", normalizedListId);
            update.ExecuteNonQuery();
        }

        TouchRelayMeta(connection, tx, normalizedRelayId);
        tx.Commit();
        return LoadPolicyListSummaryOrThrow(connection, normalizedRelayId, normalizedListId);
    }

    public IReadOnlyList<RelayPolicyListSummary> ReorderPolicyLists(string relayId, IReadOnlyList<string> orderedListIds)
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedIds = orderedListIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        var existingIds = GetRelayListIds(connection, tx, normalizedRelayId);
        if (existingIds.Count != normalizedIds.Count ||
            existingIds.Except(normalizedIds, StringComparer.Ordinal).Any() ||
            normalizedIds.Except(existingIds, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("Reorder payload must include exactly all list ids for the relay.");
        }

        // Phase 1: move all relay priorities into a temporary disjoint range to avoid
        // transient UNIQUE(relay_id, priority) collisions while reordering.
        var tempBase = 1_000_000;
        using (var moveToTemp = connection.CreateCommand())
        {
            moveToTemp.Transaction = tx;
            moveToTemp.CommandText = """
                UPDATE policy_lists
                SET priority = priority + $tempBase
                WHERE relay_id = $relayId;
                """;
            moveToTemp.Parameters.AddWithValue("$tempBase", tempBase);
            moveToTemp.Parameters.AddWithValue("$relayId", normalizedRelayId);
            moveToTemp.ExecuteNonQuery();
        }

        // Phase 2: assign final contiguous priorities in requested order.
        for (var i = 0; i < normalizedIds.Count; i++)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE policy_lists
                SET priority = $priority,
                    updated_utc = $updatedUtc
                WHERE relay_id = $relayId AND list_id = $listId;
                """;
            update.Parameters.AddWithValue("$priority", i);
            update.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$relayId", normalizedRelayId);
            update.Parameters.AddWithValue("$listId", normalizedIds[i]);
            update.ExecuteNonQuery();
        }

        TouchRelayMeta(connection, tx, normalizedRelayId);
        tx.Commit();
        return LoadRelayPolicyLists(connection, normalizedRelayId, includeEntries: false)
            .Select(x => new RelayPolicyListSummary(x.ListId, x.RelayId, x.Label, x.ListType, x.Priority, x.EntryCount))
            .ToList();
    }

    public RelayPolicyListCommitResult ReplacePolicyListEntries(string relayId, string listId, IReadOnlyList<string> entries)
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedListId = (listId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedListId))
        {
            throw new InvalidOperationException("Policy list id is required.");
        }

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

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        EnsureListOwnedByRelay(connection, tx, normalizedRelayId, normalizedListId);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM policy_entries WHERE list_id = $listId;";
            delete.Parameters.AddWithValue("$listId", normalizedListId);
            delete.ExecuteNonQuery();
        }

        var insertedCount = 0;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO policy_entries (list_id, canonical, raw, family, kind, network, prefix_len)
                VALUES ($listId, $canonical, $raw, $family, $kind, $network, $prefixLen);
                """;
            insert.Parameters.Add("$listId", SqliteType.Text);
            insert.Parameters.Add("$canonical", SqliteType.Text);
            insert.Parameters.Add("$raw", SqliteType.Text);
            insert.Parameters.Add("$family", SqliteType.Integer);
            insert.Parameters.Add("$kind", SqliteType.Integer);
            insert.Parameters.Add("$network", SqliteType.Blob);
            insert.Parameters.Add("$prefixLen", SqliteType.Integer);

            foreach (var parsed in unique.Values)
            {
                insert.Parameters["$listId"].Value = normalizedListId;
                insert.Parameters["$canonical"].Value = parsed.Canonical;
                insert.Parameters["$raw"].Value = parsed.Raw;
                insert.Parameters["$family"].Value = parsed.Family;
                insert.Parameters["$kind"].Value = parsed.Kind;
                insert.Parameters["$network"].Value = parsed.Network;
                insert.Parameters["$prefixLen"].Value = parsed.PrefixLength;
                insert.ExecuteNonQuery();
                insertedCount++;
            }
        }

        using (var touchList = connection.CreateCommand())
        {
            touchList.Transaction = tx;
            touchList.CommandText = """
                UPDATE policy_lists
                SET updated_utc = $updatedUtc
                WHERE relay_id = $relayId AND list_id = $listId;
                """;
            touchList.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            touchList.Parameters.AddWithValue("$relayId", normalizedRelayId);
            touchList.Parameters.AddWithValue("$listId", normalizedListId);
            touchList.ExecuteNonQuery();
        }

        var (revision, updatedAtUtc) = TouchRelayMeta(connection, tx, normalizedRelayId);
        var totalCount = GetListEntryCount(connection, tx, normalizedListId);
        tx.Commit();
        return new RelayPolicyListCommitResult(
            normalizedListId,
            insertedCount,
            duplicateDroppedCount,
            invalidCount,
            totalCount,
            revision,
            updatedAtUtc);
    }

    // Compatibility for legacy listType update pipeline.
    public PolicyCommitResult ApplyUpdate(string listType, string mode, IReadOnlyList<string> entries, string relayId = "")
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedListType = PolicyListTypes.Normalize(listType);
        var normalizedMode = PolicyUpdateModes.Normalize(mode);
        var legacyList = EnsureLegacyTypeList(normalizedRelayId, normalizedListType);
        if (normalizedMode == PolicyUpdateModes.Replace)
        {
            var replace = ReplacePolicyListEntries(normalizedRelayId, legacyList.ListId, entries);
            return new PolicyCommitResult(
                normalizedListType,
                normalizedMode,
                replace.AppliedCount,
                replace.DuplicateDroppedCount,
                replace.InvalidCount,
                replace.Count,
                replace.Revision,
                replace.UpdatedAtUtc);
        }

        // Merge mode: merge with existing entries and replace.
        var current = GetPolicyList(legacyList.ListId, normalizedRelayId)?.Entries ?? [];
        var merged = current.Concat(entries).ToList();
        var merge = ReplacePolicyListEntries(normalizedRelayId, legacyList.ListId, merged);
        return new PolicyCommitResult(
            normalizedListType,
            normalizedMode,
            merge.AppliedCount,
            merge.DuplicateDroppedCount,
            merge.InvalidCount,
            merge.Count,
            merge.Revision,
            merge.UpdatedAtUtc);
    }

    public bool DeletePolicyList(string relayId, string listId)
    {
        var normalizedRelayId = NormalizeRelayId(relayId);
        var normalizedListId = (listId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedListId))
        {
            return false;
        }

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        EnsureListOwnedByRelay(connection, tx, normalizedRelayId, normalizedListId);

        using (var deleteEntries = connection.CreateCommand())
        {
            deleteEntries.Transaction = tx;
            deleteEntries.CommandText = "DELETE FROM policy_entries WHERE list_id = $listId;";
            deleteEntries.Parameters.AddWithValue("$listId", normalizedListId);
            deleteEntries.ExecuteNonQuery();
        }

        var deleted = 0;
        using (var deleteList = connection.CreateCommand())
        {
            deleteList.Transaction = tx;
            deleteList.CommandText = "DELETE FROM policy_lists WHERE relay_id = $relayId AND list_id = $listId;";
            deleteList.Parameters.AddWithValue("$relayId", normalizedRelayId);
            deleteList.Parameters.AddWithValue("$listId", normalizedListId);
            deleted = deleteList.ExecuteNonQuery();
        }

        ReindexPriorities(connection, tx, normalizedRelayId);
        TouchRelayMeta(connection, tx, normalizedRelayId);
        tx.Commit();
        return deleted > 0;
    }

    public bool ImportLegacyWhitelistIfEmpty(IReadOnlyList<string> legacyEntries)
    {
        if (legacyEntries.Count == 0)
        {
            return false;
        }

        var existing = ListPolicyLists(string.Empty);
        if (existing.Count > 0)
        {
            return false;
        }

        var list = CreatePolicyList(string.Empty, "Legacy Whitelist", PolicyListTypes.Whitelist);
        _ = ReplacePolicyListEntries(string.Empty, list.ListId, legacyEntries);
        return true;
    }

    private RelayPolicyListSummary EnsureLegacyTypeList(string relayId, string listType)
    {
        var expectedLabel = string.Equals(listType, PolicyListTypes.Blacklist, StringComparison.OrdinalIgnoreCase)
            ? "Legacy Blacklist"
            : "Legacy Whitelist";
        var existing = ListPolicyLists(relayId)
            .FirstOrDefault(x => string.Equals(x.Label, expectedLabel, StringComparison.Ordinal) &&
                                 string.Equals(PolicyListTypes.Normalize(x.ListType), listType, StringComparison.OrdinalIgnoreCase));
        return existing ?? CreatePolicyList(relayId, expectedLabel, listType);
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        ResetLegacyPolicyTablesIfNeeded(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS policy_lists (
              list_id TEXT NOT NULL PRIMARY KEY,
              relay_id TEXT NOT NULL,
              label TEXT NOT NULL,
              list_type TEXT NOT NULL,
              priority INTEGER NOT NULL,
              created_utc TEXT NOT NULL,
              updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_policy_lists_relay_priority ON policy_lists(relay_id, priority);

            CREATE TABLE IF NOT EXISTS policy_entries (
              list_id TEXT NOT NULL,
              canonical TEXT NOT NULL,
              raw TEXT NOT NULL,
              family INTEGER NOT NULL,
              kind INTEGER NOT NULL,
              network BLOB NOT NULL,
              prefix_len INTEGER NOT NULL,
              PRIMARY KEY(list_id, canonical)
            );

            CREATE TABLE IF NOT EXISTS policy_meta (
              relay_id TEXT NOT NULL,
              key TEXT NOT NULL,
              value TEXT NOT NULL,
              PRIMARY KEY(relay_id, key)
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void ResetLegacyPolicyTablesIfNeeded(SqliteConnection connection)
    {
        var hasPolicyEntries = TableExists(connection, "policy_entries");
        if (!hasPolicyEntries)
        {
            return;
        }

        var columns = GetTableColumns(connection, "policy_entries");
        var hasLegacyShape = columns.Contains("relay_id", StringComparer.OrdinalIgnoreCase) &&
                             columns.Contains("list_type", StringComparer.OrdinalIgnoreCase) &&
                             !columns.Contains("list_id", StringComparer.OrdinalIgnoreCase);
        if (!hasLegacyShape)
        {
            return;
        }

        using var drop = connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE IF EXISTS policy_entries;
            DROP TABLE IF EXISTS policy_lists;
            DROP TABLE IF EXISTS policy_meta;
            """;
        drop.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static IReadOnlyList<RelayPolicyList> LoadRelayPolicyLists(SqliteConnection connection, string relayId, bool includeEntries)
    {
        var lists = new List<RelayPolicyList>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT list_id, relay_id, label, list_type, priority, updated_utc
            FROM policy_lists
            WHERE relay_id = $relayId
            ORDER BY priority ASC, list_id ASC;
            """;
        command.Parameters.AddWithValue("$relayId", relayId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var listId = reader.GetString(0);
            var listRelayId = reader.GetString(1);
            var label = reader.GetString(2);
            var listType = reader.GetString(3);
            var priority = reader.GetInt32(4);
            _ = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var entries = includeEntries ? LoadPolicyEntries(connection, listId) : [];
            var entryCount = includeEntries ? entries.Count : GetListEntryCount(connection, null, listId);
            lists.Add(new RelayPolicyList(listId, listRelayId, label, listType, priority, entries, entryCount));
        }

        return lists;
    }

    private static RelayPolicyList? LoadPolicyListById(SqliteConnection connection, string relayId, string listId, bool includeEntries)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT list_id, relay_id, label, list_type, priority
            FROM policy_lists
            WHERE relay_id = $relayId AND list_id = $listId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$relayId", relayId);
        command.Parameters.AddWithValue("$listId", listId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var entries = includeEntries ? LoadPolicyEntries(connection, listId) : [];
        var entryCount = includeEntries ? entries.Count : GetListEntryCount(connection, null, listId);
        return new RelayPolicyList(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            entries,
            entryCount);
    }

    private static RelayPolicyListSummary LoadPolicyListSummaryOrThrow(SqliteConnection connection, string relayId, string listId)
    {
        var list = LoadPolicyListById(connection, relayId, listId, includeEntries: false);
        if (list is null)
        {
            throw new InvalidOperationException("Policy list was not found.");
        }

        return new RelayPolicyListSummary(list.ListId, list.RelayId, list.Label, list.ListType, list.Priority, list.EntryCount);
    }

    private static List<string> LoadPolicyEntries(SqliteConnection connection, string listId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT canonical
            FROM policy_entries
            WHERE list_id = $listId
            ORDER BY canonical;
            """;
        command.Parameters.AddWithValue("$listId", listId);
        using var reader = command.ExecuteReader();
        var entries = new List<string>();
        while (reader.Read())
        {
            entries.Add(reader.GetString(0));
        }

        return entries;
    }

    private static List<int> GetRelayListPriorities(SqliteConnection connection, SqliteTransaction tx, string relayId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT priority FROM policy_lists WHERE relay_id = $relayId ORDER BY priority ASC;";
        command.Parameters.AddWithValue("$relayId", relayId);
        using var reader = command.ExecuteReader();
        var priorities = new List<int>();
        while (reader.Read())
        {
            priorities.Add(reader.GetInt32(0));
        }

        return priorities;
    }

    private static int ResolveInsertPriority(IReadOnlyList<int> priorities, int? preferredPriority)
    {
        var maxPriority = priorities.Count == 0 ? -1 : priorities.Max();
        if (!preferredPriority.HasValue)
        {
            return maxPriority + 1;
        }

        if (preferredPriority.Value < 0)
        {
            return 0;
        }

        return preferredPriority.Value > maxPriority + 1 ? maxPriority + 1 : preferredPriority.Value;
    }

    private static void ShiftPrioritiesAtOrAfter(SqliteConnection connection, SqliteTransaction tx, string relayId, int pivotPriority)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE policy_lists
            SET priority = priority + 1
            WHERE relay_id = $relayId AND priority >= $pivotPriority;
            """;
        command.Parameters.AddWithValue("$relayId", relayId);
        command.Parameters.AddWithValue("$pivotPriority", pivotPriority);
        command.ExecuteNonQuery();
    }

    private static List<string> GetRelayListIds(SqliteConnection connection, SqliteTransaction tx, string relayId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT list_id FROM policy_lists WHERE relay_id = $relayId ORDER BY priority ASC, list_id ASC;";
        command.Parameters.AddWithValue("$relayId", relayId);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static void ReindexPriorities(SqliteConnection connection, SqliteTransaction tx, string relayId)
    {
        var ids = GetRelayListIds(connection, tx, relayId);
        for (var i = 0; i < ids.Count; i++)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE policy_lists SET priority = $priority WHERE relay_id = $relayId AND list_id = $listId;";
            update.Parameters.AddWithValue("$priority", i);
            update.Parameters.AddWithValue("$relayId", relayId);
            update.Parameters.AddWithValue("$listId", ids[i]);
            update.ExecuteNonQuery();
        }
    }

    private static void EnsureListOwnedByRelay(SqliteConnection connection, SqliteTransaction tx, string relayId, string listId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT 1 FROM policy_lists WHERE relay_id = $relayId AND list_id = $listId LIMIT 1;";
        command.Parameters.AddWithValue("$relayId", relayId);
        command.Parameters.AddWithValue("$listId", listId);
        if (command.ExecuteScalar() is null)
        {
            throw new InvalidOperationException("Policy list was not found for relay.");
        }
    }

    private static int GetListEntryCount(SqliteConnection connection, SqliteTransaction? tx, string listId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM policy_entries WHERE list_id = $listId;";
        command.Parameters.AddWithValue("$listId", listId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static (long Revision, DateTimeOffset UpdatedAtUtc) TouchRelayMeta(SqliteConnection connection, SqliteTransaction tx, string relayId)
    {
        var revision = GetMetaLong(connection, relayId, "revision", tx) + 1;
        var updatedAtUtc = DateTimeOffset.UtcNow;
        SetMeta(connection, relayId, "revision", revision.ToString(), tx);
        SetMeta(connection, relayId, "updated_utc", updatedAtUtc.ToString("O"), tx);
        return (revision, updatedAtUtc);
    }

    private static long GetMetaLong(SqliteConnection connection, string relayId, string key, SqliteTransaction? tx = null)
    {
        return long.TryParse(GetMeta(connection, relayId, key, tx), out var value) ? value : 0L;
    }

    private static DateTimeOffset GetMetaDateTimeOffset(SqliteConnection connection, string relayId, string key, SqliteTransaction? tx = null)
    {
        return DateTimeOffset.TryParse(GetMeta(connection, relayId, key, tx), out var value) ? value : DateTimeOffset.MinValue;
    }

    private static string GetMeta(SqliteConnection connection, string relayId, string key, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT value FROM policy_meta WHERE relay_id = $relayId AND key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$relayId", relayId);
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static void SetMeta(SqliteConnection connection, string relayId, string key, string value, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO policy_meta (relay_id, key, value)
            VALUES ($relayId, $key, $value)
            ON CONFLICT(relay_id, key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$relayId", relayId);
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
        if (!hasInput || !NetworkRule.TryParse(value, out var rule, out _) || rule is null)
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

    private static string NormalizeRelayId(string relayId)
    {
        return string.IsNullOrWhiteSpace(relayId) ? "default" : relayId.Trim();
    }

    private static string NormalizeLabel(string? label)
    {
        var value = (label ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Policy list label is required.");
        }

        return value.Length > 120 ? value[..120].Trim() : value;
    }

    private sealed record ParsedPolicyEntry(string Canonical, string Raw, int Family, int Kind, byte[] Network, int PrefixLength);
}

public sealed record RelayPolicyListCommitResult(
    string ListId,
    int AppliedCount,
    int DuplicateDroppedCount,
    int InvalidCount,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

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
