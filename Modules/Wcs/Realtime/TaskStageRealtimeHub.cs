using GrcsBackend.Modules.Wcs.Automation.Models;
using GrcsBackend.Modules.Wcs.Console.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrcsBackend.Modules.Wcs.Realtime;

/// <summary>
/// 任务记录实时推送 Hub（/hubs/task-stages）。
/// 前端订阅本 Hub 接收合并表（task_records）的实时变更：
/// 新记录（创建行或阶段行）到达（TaskStageService.Record / RecordCreated）即时广播 EventAdded，
/// 删除广播 TaskRemoved，清空广播 EventsReset(空表)。
/// 连接建立时先回放全表快照（EventsReset），保证断线重连后缓存不丢。
/// </summary>
public class TaskStageRealtimeHub : Hub
{
    private readonly ITaskStageService _stages;

    public TaskStageRealtimeHub(ITaskStageService stages) => _stages = stages;

    public override async Task OnConnectedAsync()
    {
        // 回放全表快照（创建行 + 阶段行，等价于旧轮询的全量首拉），让新连接立即有数据
        await Clients.Caller.SendAsync("EventsReset", _stages.GetAll());
        await base.OnConnectedAsync();
    }
}