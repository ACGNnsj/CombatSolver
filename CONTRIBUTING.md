# 参与 CombatSolver 开发

从[社区任务入口](https://github.com/Torch1230/CombatSolver/issues/171)认领故障或路线优化批次。**每批五个主题，每主题一至两个代表包，一个 issue，一次认领整批**。卡牌和报告是主题样本，认领与验收按机制主题组织。

**建议优先认领并修复 Bug 批次，再处理更优世界线优化批次。** 世界线优化开始前，先确认样本的模拟状态与部署行为正确，再比较路线质量。

当前材料只取 **0.47.x 未修复报告**。分发前阅读最早异常、动作窗口及源码，归并共同机制；没有运行恢复、搜索或部署。静态已定位原因与共同机制待证分别标注，当前源码行为仍由认领者验证。重复主题包直接清理并跳过，不继续补样本。

社区任务面向**原版游戏内容**。主项目不主动适配修改游戏内容的第三方 Mod；相关角色、卡牌、Power、遗物、怪物与战斗逻辑的支持请求单独留档。第三方场景里的通用异常，需要取得原版内容证据后进入认领队列。

## 认领与协作

1. 选择一个开放、标记“未认领”的批次 issue，回复“认领 B012 整批”或“认领 Q003 整批”（也支持单独回复 `认领` / `/claim`）。自动指派回复者，负责全部五个主题；入口表格显示链接到 GitHub 主页的用户名。每次认领一批，完成或交接后再领取下一批。
2. 阅读 [AGENTS.md](AGENTS.md)、任务正文及[夹具与脚手架指南](docs/community/testing-guide.md)。AI 助手同样遵守这些规则。
3. 从最新 `main` 开分支，例如 `fix/batch-b012` 或 `perf/batch-q002`。按主题推进和记录，可分阶段 PR。涉及多模块所有权、模拟机制、公共 API 或搜索架构的大改动，先写方案、范围与验收口径，确认范围后实施。
4. 同主题出现不同首因时，依据调用链与最小证据补充子问题，保留相关性。共同报错入口是机制线索。无需逐个旧卡牌编号修复或逐包复跑。

在自己认领的批次回复 `取消认领`、`放弃认领` 或 `/unclaim`，释放认领。已有负责人、批次已关闭或本人已有其他开放批次时，新认领不生效；以 Issue 的 Assignee 和入口表格为准。交接由维护者调整 Assignee，表格同步更新。删除或编辑旧回复不会取消认领，请发一条新的取消回复。

认领命令放在回复首行；`认领 B012 整批` 后可补充计划。GitHub Actions 在回复或指派变更后更新表格，通常几秒到一分钟，繁忙时可能排队。

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
- PR 关联批次 issue，列已处理主题、验证结果与剩余主题。阶段用 `Refs #批次议题号`，五个主题全部验收后才 `Closes #批次议题号`。旧条目编号仅作迁移来源。
- 战斗语义、搜索或测试行为变化同步开发记录和测试矩阵。新最小夹具放相应目录，文档变化同步导航。
- 提交源码、最小夹具和简洁记录。原始包、完整日志、游戏 DLL、反编译源码、凭据、个人路径和实验产物留在忽略目录。
- 玩家路线用于定位缺口；生产规则按通用效果、状态和兑现时机表达。

维护者审阅并合并。游戏测试依赖本机安装，目前没有替代这些验证的通用云端全量 CI；PR 提供实际本地证据。按主题验收，全批完成后关闭批次，后台修复状态依据实际修复证据处理。

## 资料批次

任务材料使用独立 `community-tasks-*` Release，客户端安装包使用 `v*` Release。范围、去重口径与清单见[任务资料说明](docs/community/task-data.md)。讨论留在 issue/PR，方便接手。公开材料清理身份与本机路径；提交证据时保留模型 ID、牌序、RNG 和状态差异。
