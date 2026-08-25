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
            -- 任务类型模板（前端任务下发页创建/维护；payload 存完整模板 JSON，跨浏览器共享）
            CREATE TABLE IF NOT EXISTS task_templates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                value TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL,
                description TEXT NOT NULL,
                category TEXT NOT NULL,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            -- 功能模板（前端信号交互页创建/维护；payload 存完整模块 JSON，跨浏览器共享）
            CREATE TABLE IF NOT EXISTS feature_modules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                module_id TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                api_url TEXT NOT NULL,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            -- 自动化模板（前端自动化页创建/维护；payload 存完整模板 JSON，跨浏览器共享）
            CREATE TABLE IF NOT EXISTS auto_templates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                template_id TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            -- 通用 Mock 规则（前端可配任意入站 URL + 参数匹配 → 自定义返回值）
            CREATE TABLE IF NOT EXISTS mock_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rule_id TEXT NOT NULL UNIQUE,
                method TEXT NOT NULL,
                path_pattern TEXT NOT NULL,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL
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

    public void KvRemove(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
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

    // ── Task Templates（任务类型模板，payload 存完整 JSON）──

    /// <summary>全表读取（id 升序）。</summary>
    public List<Models.TaskTemplateDto> TaskTemplateGetAll()
    {
        var list = new List<Models.TaskTemplateDto>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value, label, description, category, payload FROM task_templates ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dto = Deserialize<Models.TaskTemplateDto>(reader.GetString(4));
            if (dto == null) continue;
            dto.Value = reader.GetString(0);
            dto.Label = reader.GetString(1);
            dto.Description = reader.GetString(2);
            dto.Category = reader.GetString(3);
            list.Add(dto);
        }
        return list;
    }

    /// <summary>整体替换（删除旧行后按序插入；Value 冲突时更新）。</summary>
    public void TaskTemplateReplaceAll(IEnumerable<Models.TaskTemplateDto> items)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM task_templates";
            del.ExecuteNonQuery();
        }
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Value)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO task_templates(value, label, description, category, payload, updated_at)
                VALUES($v,$l,$d,$c,$p,$u)
                ON CONFLICT(value) DO UPDATE SET label=excluded.label, description=excluded.description,
                    category=excluded.category, payload=excluded.payload, updated_at=excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$v", item.Value);
            cmd.Parameters.AddWithValue("$l", item.Label ?? "");
            cmd.Parameters.AddWithValue("$d", item.Description ?? "");
            cmd.Parameters.AddWithValue("$c", item.Category ?? "");
            cmd.Parameters.AddWithValue("$p", System.Text.Json.JsonSerializer.Serialize(item));
            cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>按 Value 删除一条任务模板。</summary>
    public void TaskTemplateRemove(string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_templates WHERE value = $v";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ── Feature Modules（功能模板，payload 存完整 JSON）──

    /// <summary>全表读取（id 升序）。</summary>
    public List<Models.FeatureModuleDto> FeatureModuleGetAll()
    {
        var list = new List<Models.FeatureModuleDto>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT module_id, name, api_url, payload FROM feature_modules ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dto = Deserialize<Models.FeatureModuleDto>(reader.GetString(3));
            if (dto == null) continue;
            dto.Id = reader.GetString(0);
            dto.Name = reader.GetString(1);
            dto.ApiUrl = reader.GetString(2);
            list.Add(dto);
        }
        return list;
    }

    /// <summary>整体替换（删除旧行后按序插入；module_id 冲突时更新）。</summary>
    public void FeatureModuleReplaceAll(IEnumerable<Models.FeatureModuleDto> items)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM feature_modules";
            del.ExecuteNonQuery();
        }
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO feature_modules(module_id, name, api_url, payload, updated_at)
                VALUES($i,$n,$a,$p,$u)
                ON CONFLICT(module_id) DO UPDATE SET name=excluded.name, api_url=excluded.api_url,
                    payload=excluded.payload, updated_at=excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$i", item.Id);
            cmd.Parameters.AddWithValue("$n", item.Name ?? "");
            cmd.Parameters.AddWithValue("$a", item.ApiUrl ?? "");
            cmd.Parameters.AddWithValue("$p", System.Text.Json.JsonSerializer.Serialize(item));
            cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>按 module_id 删除一条功能模板。</summary>
    public void FeatureModuleRemove(string moduleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM feature_modules WHERE module_id = $i";
        cmd.Parameters.AddWithValue("$i", moduleId);
        cmd.ExecuteNonQuery();
    }

    // ── Auto Templates（自动化模板，payload 存完整 JSON）──

    /// <summary>全表读取（id 升序）。</summary>
    public List<Models.AutoTemplateDto> AutoTemplateGetAll()
    {
        var list = new List<Models.AutoTemplateDto>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id, name, payload FROM auto_templates ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dto = Deserialize<Models.AutoTemplateDto>(reader.GetString(2));
            if (dto == null) continue;
            dto.Id = reader.GetString(0);
            dto.Name = reader.GetString(1);
            list.Add(dto);
        }
        return list;
    }

    /// <summary>整体替换（删除旧行后按序插入；template_id 冲突时更新）。</summary>
    public void AutoTemplateReplaceAll(IEnumerable<Models.AutoTemplateDto> items)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM auto_templates";
            del.ExecuteNonQuery();
        }
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO auto_templates(template_id, name, payload, updated_at)
                VALUES($i,$n,$p,$u)
                ON CONFLICT(template_id) DO UPDATE SET name=excluded.name,
                    payload=excluded.payload, updated_at=excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$i", item.Id);
            cmd.Parameters.AddWithValue("$n", item.Name ?? "");
            cmd.Parameters.AddWithValue("$p", System.Text.Json.JsonSerializer.Serialize(item));
            cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>按 template_id 删除一条自动化模板。</summary>
    public void AutoTemplateRemove(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM auto_templates WHERE template_id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    // ── Mock Rules（通用入站 Mock）──

    public List<Models.MockRuleDto> MockRuleGetAll()
    {
        var list = new List<Models.MockRuleDto>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rule_id, method, path_pattern, payload FROM mock_rules ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dto = Deserialize<Models.MockRuleDto>(reader.GetString(3));
            if (dto == null) continue;
            dto.Id = reader.GetString(0);
            dto.Method = reader.GetString(1);
            dto.PathPattern = reader.GetString(2);
            list.Add(dto);
        }
        return list;
    }

    public void MockRuleReplaceAll(IEnumerable<Models.MockRuleDto> items)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand()) { del.CommandText = "DELETE FROM mock_rules"; del.ExecuteNonQuery(); }
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mock_rules(rule_id, method, path_pattern, payload, updated_at)
                VALUES($i,$m,$p,$l,$u)
                ON CONFLICT(rule_id) DO UPDATE SET method=excluded.method, path_pattern=excluded.path_pattern, payload=excluded.payload, updated_at=excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$i", item.Id);
            cmd.Parameters.AddWithValue("$m", item.Method ?? "POST");
            cmd.Parameters.AddWithValue("$p", item.PathPattern ?? "");
            cmd.Parameters.AddWithValue("$l", System.Text.Json.JsonSerializer.Serialize(item));
            cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void MockRuleRemove(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mock_rules WHERE rule_id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    private static T? Deserialize<T>(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    private static List<string> DeserializeCodes(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
