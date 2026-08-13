using GrcsBackend.Modules.Wcs.Services;

namespace GrcsBackend.Modules.Wcs;

/// <summary>
/// Wcs 模块的依赖注入注册。
/// 新增模块时在 Modules/ 下建平级目录，Program.cs 中挂接对应扩展方法。
/// </summary>
public static class WcsModuleExtensions
{
    public static IServiceCollection AddWcsModule(this IServiceCollection services)
    {
        // 准入决策状态必须跨请求共享（GRCS 循环重发 + 管理接口查询），用 Singleton
        services.AddSingleton<IAdmittanceService, AdmittanceService>();
        // 任务阶段事件同样跨请求共享（GRCS 上报 + 前端轮询），用 Singleton
        services.AddSingleton<ITaskStageService, TaskStageService>();
        return services;
    }
}
