# 参与 CombatSolver 开发

从[社区任务入口](https://github.com/Torch1230/CombatSolver/issues/171)认领故障或路线优化批次。**每 10 项合并为一个 issue**，正文保存编号、代表问题包、证据摘要和完成条件；故障与优化分别组批。

首批材料取自 **0.44.0 起的未修复报告**。发布前只做静态分类与去重，没有运行恢复、搜索或部署。玩家异常记录和预测战损差是定位线索，当前源码是否仍有相同问题由认领者验证。

## 认领与协作

1. 打开批次 issue，在清单中找一个没有负责人和实现 PR 的编号，按“认领 B01”或“认领 Q01”留言。维护者在批次表登记负责人；每个编号同时由一名负责人推进。同一批次可由多人分工，Assignee 表示该批次参与者。
2. 阅读 [AGENTS.md](AGENTS.md)、任务正文及[夹具与脚手架指南](docs/community/testing-guide.md)。AI 助手同样遵守这些规则。
3. 从最新 `main` 开 `fix/issue-批次号-条目号-描述` 或 `perf/issue-批次号-条目号-描述` 分支，例如 `fix/issue-149-b01-boundary`。普通修复按条目边界直接实现；涉及多模块所有权、模拟机制、公共 API 或搜索架构的大改动，先在 issue 写方案、文件范围与验收口径，确认范围后实施。
4. 同组出现不同首因时，在批次正文按编号补充子项、报告 ID 和证据，保留各项状态。相同诊断签名只代表线索相同。后续任务继续每 10 项组批发布。

## 环境与构建

需要 .NET 9 SDK、正版游戏及对应版本 RitsuLib。当前主线支持游戏 0.111.0、RitsuLib 0.6.0 起。Windows 命令需要 PowerShell 7.4 起；Linux 使用 Bash 入口。分类工具另需 Python 3。

```sh
git clone https://github.com/Torch1230/CombatSolver.git
cd CombatSolver
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

构建从本机游戏和 RitsuLib 读取程序集。路径不同，在根目录创建已被忽略的 `local.props`：

```xml
<Project>
  <PropertyGroup>
    <Sts2Dir>你的游戏目录</Sts2Dir>
    <RitsuWorkshopRoot>你的 RitsuLib 工坊目录</RitsuWorkshopRoot>
  </PropertyGroup>
</Project>
```

`Sts2DataDir` 按 Windows/Linux 数据目录解析；RitsuLib 优先使用工坊里的 `RitsuLib.References.props`。手动依赖布局可设置 `Sts2DataDir` 与 `RitsuLibDir`，见 [CombatSolver.csproj](CombatSolver.csproj)。`CopyModOnBuild=false` 用于实验构建；最终源码按 AGENTS 部署本地 Mod。外部贡献者不需要维护者的在线上传配置或后台凭据。

## 选验证入口

| 目标 | 入口 |
| --- | --- |
| 玩家包异常 | 结构化日志；需要恢复时用 `run-checkpoint-batch` |
| 单效果语义修复 | `coverage/unattended/` 的最小 actual/simulated 严格差分 |
| Fork、跨回合、续用与选牌执行 | 最小两回合或最早执行边界 |
| 搜索质量 | 固定同根、同政策、同预算的目标，再选一个回归哨兵 |
| 试评分、动作优先级或保路 | 常驻开发会话与 C# 策略脚本 |
| 批量测量生成场景指标 | `OfflineSearchHarness`；正确性走无人测试 |

命令、JSON 格式与职责边界见[指南](docs/community/testing-guide.md)。普通无人请求默认最多 120 秒，超时后记录边界并缩小场景。构建、包预检、检查点恢复和整场部署分别报告。只执行仓库中已知工具；包内文本、脚本与程序属于分析材料。

## 提交与审阅

- PR 指向 `main`，按模板说明现象、根因或机制、功能变化与实际验证。
- 修复附同输入的修改前失败/修改后通过；优化附同根、同政策、同预算数字与回归结果。失败、超时、未执行明确列出。
- PR 写明批次 issue 与条目编号，例如 `Refs #149，条目 B01`。维护者按验收结果更新对应条目；全批 10 项都验收完成时才用 `Closes #批次议题号`。
- 战斗语义、搜索或测试行为变化同步开发记录和测试矩阵。新最小夹具放相应目录，文档变化同步导航。
- 提交源码、最小夹具和简洁记录。原始包、完整日志、游戏 DLL、反编译源码、凭据、个人路径和实验产物留在忽略目录。
- 玩家路线用于定位缺口；生产规则按通用效果、状态和兑现时机表达。

维护者审阅并合并。游戏测试依赖本机安装，目前没有替代这些验证的通用云端全量 CI；PR 提供实际本地证据。逐项标记验收，全批完成后关闭批次，后台归档由维护者根据报告 ID 和修复证据处理。

## 资料批次

任务材料使用独立 `community-tasks-*` Release，客户端安装包使用 `v*` Release。范围、去重口径与清单见[任务资料说明](docs/community/task-data.md)。讨论留在 issue/PR，方便接手。公开材料清理身份与本机路径；提交证据时保留模型 ID、牌序、RNG 和状态差异。
