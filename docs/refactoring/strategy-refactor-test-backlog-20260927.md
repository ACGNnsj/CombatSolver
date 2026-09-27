# 策略重构待测清单（2026-09-27）

背景：用户正在运行游戏，本轮起**禁止启动任何实例**（headless 与可见 Steam 都不启动）。因此重构批次只执行 Release 编译与 Windows 静态结构门禁；所有需要实例的行为对照一律记为待测，在实测通过前不宣称行为等价。

分支：`refactor/strategy-search-p0-20260927`。

## 本轮已改（P2 外层补搜 Pass 抽取）

把外层 `Solve` 的补搜块抽到 `CombatSearchCoordinator.PostSearch.cs`，并把 `RunEarlyTurnExploration` 改为接收 `SearchPassContext`：

- `RunEarlyPotionPairRescue`（已有，本轮确认仍在 PostSearch）
- `RunForcedPotionOpeningRescue`
- `RunTurnBoundaryRescue`
- `RunZeroCostOpeningRescue`
- `RunMidCombatRefinement`
- `RunTurnEndChoicePosterior`
- `RunEarlierCopyDelayedDamage`
- `RunEarlyTurnExploration` 改为 `(SearchPassContext, SolverResult)` 形式

抽取方式：纯移动，方法体、分支条件、派发顺序、诊断标签与预算读取时点保持不变；仅把 `enrichedProgressCallback` 改名为局部 `progressCallback`，并把 `RunEarlyTurnExploration` 的散参数收敛为 `context`。

## 本轮已取得的直接证据

- `dotnet build CombatSolver.csproj -c Release`：0 警告 0 错误。
- `pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=219`。
- `CombatSearchCoordinator.cs`：约 3,032 → 2,596 行；`CombatSearchCoordinator.PostSearch.cs` 709 行。

这三项只证明“能编译、结构门禁未破”，**不是行为等价证据**。

## 待测项（需要实例，暂缓）

1. **P2 收口一次对照**：使用 `coverage/strategy-refactor-p2/corpus.json`，将 #24、#37、#81、#89 和两个生成场景与已保存的 `baseline-0471` 比较动作、结果、工作量和剪枝计数。#79、#85 的限时基线不参与逐位门槛。该次运行也作为穿过本轮补搜边界的代表场景；不为每个模式另跑一包。
2. **本地 Mod 部署**：用户游戏中，本轮未部署。待游戏退出后，把与最终源码一致的五个 Mod 文件覆盖到已确认的 `mods/CombatSolver`，不启动游戏。

Release 构建与 Windows 结构门禁已通过；Linux 门禁按用户要求不运行。上述行为对照未完成前，不能宣称 P2 逐位等价。

## 后续阶段待测（占位）

- **P3 续搜框架**：前缀身份、去重、派发顺序、诊断标签逐位对照；协调器主文件目标 ≤1,200 行。
- **P4 登记表**：迁移对象行为等价；新增一个测试登记项不修改搜索主流程即可产生行为。
- **P5 展开统一**：DOP1 与 DOP8 路线一致，选择预算、512 回放、原序提交不变。
- **P6 计划层**：同根同预算下 `#100`、`#101`（不足两包时加 `#84`）至少两包优于当前求解器，并保留已达标哨兵；不作预算扩张凑结果。

## 记录规则

每项实测通过后才写入“实测证据 + 命令”；不把构建或静态门禁当行为通过；无法执行时明确写未验证。纯重构出现无法解释的差异时停在该边界，不带入下一阶段。
