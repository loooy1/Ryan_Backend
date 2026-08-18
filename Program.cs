using GrcsBackend.Modules.Automation;
using GrcsBackend.Modules.Wcs;

var builder = WebApplication.CreateBuilder(args);

// ── 本项目定位 ──
// WCS 后端（管理面）：对 GRCS 核心系统暴露 WCS 协议回调接口（/api/v1/*），
// 对 WCS 前端提供控制台查询/管理接口（/api/wcs/*）。监听端口由 appsettings.json 的
// Urls 决定（默认 http://0.0.0.0:8230）。GRCS 核心后端（8224）是另一个系统，不在本仓库。
//
// ── 模块化约定 ──
// 每个业务域一个 Modules/<域>/ 目录（Controllers/Models/Services），
// 通过 Modules/<域>/XxxModuleExtensions.AddXxxModule() 在此挂接注册。

// 控制器 + NewtonsoftJson：GRCS 按 "yyyy-MM-dd HH:mm:ss.fff" 反序列化响应中的 MsgTime，
// 序列化端必须用同一格式，否则 GRCS 解析失败会把外围作业置为异常。
builder.Services.AddControllers().AddNewtonsoftJson(options =>
{
    options.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss.fff";
});

// CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务。
// 生产环境应收紧为前端实际域名。
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// 模块注册（后续新增模块在此挂接）
builder.Services.AddWcsModule();
builder.Services.AddAutomationModule();

var app = builder.Build();

app.UseCors();
app.MapControllers();

app.Run();
