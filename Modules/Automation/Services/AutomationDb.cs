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
            -- 任务阶段事件（GRCS task_stage_change 流水，持久化后重启不丢）
            CREATE TABLE IF NOT EXISTS task_stage_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                stage TEXT NOT NULL,
                time TEXT NOT NULL,
                warehouse TEXT,
                station_code TEXT,
                container_code TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_tse_task ON task_stage_events(task_id);
            -- 信号确认状态（kind = arrival / removal / sent；同任务同类最多一行，value 存分拣编辑参数 JSON）
            CREATE TABLE IF NOT EXISTS workflow_state (
                kind TEXT NOT NULL,
                task_id TEXT NOT NULL,
                value TEXT,
                time TEXT NOT NULL,
                PRIMARY KEY (kind, task_id)
            );
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

    // ── Task Stage Events（SQLite 持久化；TaskStageService 读写，重启不丢）──

    public void StageInsert(GrcsBackend.Modules.Wcs.Models.StageChangeEvent evt)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO task_stage_events(task_id, stage, time, warehouse, station_code, container_code)
                VALUES($t,$s,$m,$w,$sc,$cc)
                """;
            cmd.Parameters.AddWithValue("$t", evt.TaskId);
            cmd.Parameters.AddWithValue("$s", evt.Stage);
            cmd.Parameters.AddWithValue("$m", evt.Time.ToString("O"));
            cmd.Parameters.AddWithValue("$w", evt.Warehouse);
            cmd.Parameters.AddWithValue("$sc", evt.StationCode);
            cmd.Parameters.AddWithValue("$cc", evt.ContainerCode);
            cmd.ExecuteNonQuery();
        }
        // 上限 5000 条，超出删最旧
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM task_stage_events WHERE id NOT IN (SELECT id FROM task_stage_events ORDER BY id DESC LIMIT 5000)";
            del.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void StageRemoveByTaskId(string taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_stage_events WHERE task_id = $t";
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.ExecuteNonQuery();
    }

    public void StageClear()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_stage_events";
        cmd.ExecuteNonQuery();
    }

    public List<GrcsBackend.Modules.Wcs.Models.StageChangeEvent> StageLoadAll()
    {
        var list = new List<GrcsBackend.Modules.Wcs.Models.StageChangeEvent>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, task_id, stage, time, warehouse, station_code, container_code FROM task_stage_events ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GrcsBackend.Modules.Wcs.Models.StageChangeEvent
            {
                Id = reader.GetInt64(0),
                TaskId = reader.GetString(1),
                Stage = reader.GetString(2),
                Time = ParseRoundtrip(reader.GetString(3)),
                Warehouse = reader.GetString(4),
                StationCode = reader.GetString(5),
                ContainerCode = reader.GetString(6),
            });
        }
        return list;
    }

    private static DateTime ParseRoundtrip(string s)
    {
        try
        {
            return DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
        }
        catch { return DateTime.MinValue; }
    }

    // ── Workflow State（信号确认：kind = arrival / removal / sent）──

    /// <summary>写入确认状态（幂等抢占）：新插入返回 true（claimed），已存在返回 false。</summary>
    public bool WorkflowSet(string kind, string taskId, string? value, string time)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO workflow_state(kind, task_id, value, time)
            VALUES($k,$t,$v,$m)
            """;
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", time);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void WorkflowRemove(string kind, string taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM workflow_state WHERE kind = $k AND task_id = $t";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.ExecuteNonQuery();
    }

    public List<Models.WorkflowStateRow> WorkflowGetAll()
    {
        var list = new List<Models.WorkflowStateRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, task_id, value, time FROM workflow_state";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Models.WorkflowStateRow
            {
                Kind = reader.GetString(0),
                TaskId = reader.GetString(1),
                Value = reader.IsDBNull(2) ? null : reader.GetString(2),
                Time = reader.GetString(3),
            });
        }
        return list;
    }

    private static List<string> DeserializeCodes(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
