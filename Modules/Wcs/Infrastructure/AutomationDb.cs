using Microsoft.Data.Sqlite;

namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// SQLite 基础设施（单文件 grcs.db 放 ContentRoot）。
/// 表：kv（键值，存范围配置/运行配置/地图缓存/货物码映射）、task_records（任务合并表：创建行+阶段行，上限 10000 条）
/// 与 workflow_state（信号确认状态）。
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
            -- 旧结构已合并进 task_records，残留表直接删除（旧数据不迁移，用户全清）
            DROP TABLE IF EXISTS ledger;
            DROP TABLE IF EXISTS task_stage_events;
            -- 任务合并表：一条记录 = 一个 TaskId 的一个状态快照
            -- stage = CREATED（WCS 下发时写，含台账字段）/ START / LOAD_FINISH / FINISHED（GRCS 阶段回调）
            -- route_codes 存创建行的站点对 JSON（原 ledger.station_code），station_code 存阶段行的单站点，语义分离
            CREATE TABLE IF NOT EXISTS task_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                stage TEXT NOT NULL,
                time TEXT NOT NULL,
                warehouse TEXT NOT NULL,
                container_code TEXT NOT NULL,
                cargo_code TEXT NOT NULL,
                task_type TEXT NOT NULL,
                route_codes TEXT NOT NULL,
                station_code TEXT NOT NULL,
                ok INTEGER NOT NULL,
                status_code INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tr_task ON task_records(task_id);
            CREATE INDEX IF NOT EXISTS idx_tr_stage ON task_records(stage);
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

    // ── Task Records（合并表：创建行 CREATED + 阶段行，上限 10000 条）──

    /// <summary>插入一条记录（忽略 rec.Id，DB 自增分配），返回新 Id；超出 10000 条删最旧。</summary>
    public long TaskRecordInsert(Models.TaskRecord rec)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        long newId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO task_records(task_id, stage, time, warehouse, container_code, cargo_code, task_type, route_codes, station_code, ok, status_code)
                VALUES($t,$s,$m,$w,$cc,$g,$y,$rc,$sc,$o,$st)
                """;
            cmd.Parameters.AddWithValue("$t", rec.TaskId);
            cmd.Parameters.AddWithValue("$s", rec.Stage);
            cmd.Parameters.AddWithValue("$m", rec.Time.ToString("O"));
            cmd.Parameters.AddWithValue("$w", rec.Warehouse);
            cmd.Parameters.AddWithValue("$cc", rec.ContainerCode);
            cmd.Parameters.AddWithValue("$g", rec.CargoCode);
            cmd.Parameters.AddWithValue("$y", rec.TaskType);
            cmd.Parameters.AddWithValue("$rc", System.Text.Json.JsonSerializer.Serialize(rec.RouteCodes));
            cmd.Parameters.AddWithValue("$sc", rec.StationCode);
            cmd.Parameters.AddWithValue("$o", rec.Ok ? 1 : 0);
            cmd.Parameters.AddWithValue("$st", rec.StatusCode);
            cmd.ExecuteNonQuery();
        }
        using (var idc = conn.CreateCommand())
        {
            idc.CommandText = "SELECT last_insert_rowid()";
            newId = Convert.ToInt64(idc.ExecuteScalar());
        }
        // 上限 10000 条，超出删最旧
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM task_records WHERE id NOT IN (SELECT id FROM task_records ORDER BY id DESC LIMIT 10000)";
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return newId;
    }

    /// <summary>全表读取（id 升序，创建行与阶段行混排）。</summary>
    public List<Models.TaskRecord> TaskRecordGetAll()
    {
        var list = new List<Models.TaskRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, task_id, stage, time, warehouse, container_code, cargo_code, task_type, route_codes, station_code, ok, status_code FROM task_records ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadTaskRecord(reader));
        return list;
    }

    /// <summary>读创建行（stage=CREATED，投影为台账条目；id 倒序，最新在前）。</summary>
    public List<Models.TaskLedgerEntry> TaskRecordGetCreated(int limit = 500)
    {
        var list = new List<Models.TaskLedgerEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, task_id, stage, time, warehouse, container_code, cargo_code, task_type, route_codes, station_code, ok, status_code FROM task_records WHERE stage = 'CREATED' ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 10000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadTaskRecord(reader).ToLedgerEntry());
        return list;
    }

    /// <summary>读阶段行（stage<>'CREATED'，投影为阶段事件；id 升序）。</summary>
    public List<GrcsBackend.Modules.Wcs.Console.Models.StageChangeEvent> TaskRecordGetStages()
    {
        var list = new List<GrcsBackend.Modules.Wcs.Console.Models.StageChangeEvent>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, task_id, stage, time, warehouse, container_code, cargo_code, task_type, route_codes, station_code, ok, status_code FROM task_records WHERE stage <> 'CREATED' ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadTaskRecord(reader).ToStageEvent());
        return list;
    }

    /// <summary>删除指定任务的全部行（创建行 + 阶段行）。</summary>
    public void TaskRecordRemoveByTaskId(string taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_records WHERE task_id = $t";
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>清空全表。</summary>
    public void TaskRecordClear()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_records";
        cmd.ExecuteNonQuery();
    }

    private static Models.TaskRecord ReadTaskRecord(SqliteDataReader reader)
    {
        return new Models.TaskRecord
        {
            Id = reader.GetInt64(0),
            TaskId = reader.GetString(1),
            Stage = reader.GetString(2),
            Time = ParseRoundtrip(reader.GetString(3)),
            Warehouse = reader.GetString(4),
            ContainerCode = reader.GetString(5),
            CargoCode = reader.GetString(6),
            TaskType = reader.GetString(7),
            RouteCodes = DeserializeCodes(reader.GetString(8)),
            StationCode = reader.GetString(9),
            Ok = reader.GetInt32(10) != 0,
            StatusCode = reader.GetInt32(11),
        };
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
