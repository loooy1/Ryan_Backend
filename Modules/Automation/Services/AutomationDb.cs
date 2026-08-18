using Microsoft.Data.Sqlite;

namespace GrcsBackend.Modules.Automation.Services;

/// <summary>
/// SQLite 基础设施（单文件 grcs.db 放 ContentRoot）。
/// 两张表：kv（键值，存范围配置/运行配置/地图缓存/货物码映射）与 ledger（任务台账，上限 2000 条）。
/// 锁与运行状态纯内存（进程重启即失效，符合 Skill E 设计）。
/// </summary>
public class AutomationDb
{
    private readonly string _connStr;

    public AutomationDb(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "grcs.db");
        _connStr = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Init();
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS kv (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ledger (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                task_type TEXT NOT NULL,
                container_code TEXT NOT NULL,
                cargo_code TEXT NOT NULL,
                station_code TEXT NOT NULL,
                warehouse TEXT NOT NULL,
                time TEXT NOT NULL,
                ok INTEGER NOT NULL,
                status_code INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ledger_id ON ledger(id);
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    // ── KV ──

    public string? KvGet(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void KvSet(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ── Ledger ──

    public void LedgerAppend(List<Models.TaskLedgerEntry> entries)
    {
        if (entries.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var e in entries)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ledger(task_id, task_type, container_code, cargo_code, station_code, warehouse, time, ok, status_code)
                VALUES($t,$y,$c,$g,$s,$w,$m,$o,$sc)
                """;
            cmd.Parameters.AddWithValue("$t", e.TaskId);
            cmd.Parameters.AddWithValue("$y", e.TaskType);
            cmd.Parameters.AddWithValue("$c", e.ContainerCode);
            cmd.Parameters.AddWithValue("$g", e.CargoCode);
            cmd.Parameters.AddWithValue("$s", System.Text.Json.JsonSerializer.Serialize(e.StationCode));
            cmd.Parameters.AddWithValue("$w", e.Warehouse);
            cmd.Parameters.AddWithValue("$m", e.Time);
            cmd.Parameters.AddWithValue("$o", e.Ok ? 1 : 0);
            cmd.Parameters.AddWithValue("$sc", e.StatusCode);
            cmd.ExecuteNonQuery();
        }
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM ledger WHERE id NOT IN (SELECT id FROM ledger ORDER BY id DESC LIMIT 2000)";
            del.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<Models.TaskLedgerEntry> LedgerGet(int limit = 500)
    {
        var list = new List<Models.TaskLedgerEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT task_id, task_type, container_code, cargo_code, station_code, warehouse, time, ok, status_code FROM ledger ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 2000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Models.TaskLedgerEntry
            {
                TaskId = reader.GetString(0),
                TaskType = reader.GetString(1),
                ContainerCode = reader.GetString(2),
                CargoCode = reader.GetString(3),
                StationCode = DeserializeCodes(reader.GetString(4)),
                Warehouse = reader.GetString(5),
                Time = reader.GetString(6),
                Ok = reader.GetInt32(7) != 0,
                StatusCode = reader.GetInt32(8),
            });
        }
        return list;
    }

    public void LedgerClear()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ledger";
        cmd.ExecuteNonQuery();
    }

    private static List<string> DeserializeCodes(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
