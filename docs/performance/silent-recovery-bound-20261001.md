# 静默猎手基础根的生命界认证（2026-10-01）

## 本次范围

扩展 `src/Search/StrategicHpRecoveryBound.cs` 的封闭来源集合：新增静默猎手、其四种基础牌及起始遗物。根捕获仍在主线程把认证冻结为只读 `HasOnlyPostCombatHealing`，搜索沿用既有生命下界，不新增分支状态、状态键、续用字段或预算。该认证既影响主搜索的原有生命界，也影响早期回合探索的外部界。

原有门禁保持：药水、未认证卡牌／遗物／玩家 Power、附魔／灾厄、Modifier、第三方 subscriber、BaseLib 卡牌修正及适配 OnPlay 来源都会保留原来的完整缺血余量。没有扩大敌人认证范围，没有改变 rank 窗口、候选顺序、路线比较或早停规则。

## 原版来源的闭合核对

从当前安装的官方游戏 DLL 定向读取声明方法和异步 `MoveNext`，并核对相关基类默认 Hook。模块 ID 为 `73b63ee0-6c0a-47bb-b0d1-b21f6d94222e`；原始 IL 记录在忽略目录 `.local/silent-hp-bound-audit/closure-il.txt`。

| 来源 | 已核对效果 |
|---|---|
| `Silent` | 起始牌组为五张 `StrikeSilent`、五张 `DefendSilent`、`Neutralize` 和 `Survivor`；无角色固有战斗 Hook |
| `StrikeSilent` | 攻击敌人 |
| `DefendSilent` | 获得格挡 |
| `Neutralize` | 攻击敌人并施加虚弱 |
| `Survivor` | 获得格挡，从现有手牌选择并弃置一张 |
| `RingOfTheSnake` | 本人首回合抽牌数增加两张，只访问已有牌 |

上述类型均为 sealed，不能由第三方派生类绕过类型门禁。升级只改变伤害、格挡或虚弱数值，不新增效果来源。通用卡牌包装仍可能访问附魔／灾厄，因此两项为空的原门禁继续保留；允许集合中没有弃牌治疗或生成治疗的钩子。

这次证明限定新增静默猎手来源，不重新宣称全部原版怪物或全部 Mod 内容的治疗上界已经得到验证。

## 原生最小验证

同一隔离无头进程 PID47772 连续执行三个请求，后两项 `reusedProcess=true`；所有请求总超时为 120 秒，最后一项带 `-CleanupInstanceOnExit`，启动器输出已删除整个 `.local/headless-instances/silent-recovery-native-20261001`。

| 请求 | runId | 验证与结果 |
|---|---|---|
| `HEAL-BOUND-SAFE-ROOT` / SILENT | `5f52ea1c811e48528784550f0f773611` | Passed；普通／升级基础牌及起始遗物的根认证为 true，主线程隔离和 Fork 合同通过；原生／模拟执行四张升级基础牌、生存者显式弃牌及当前怪物行动，按现有 MoveState 与 RNG 差分通过 |
| `HEAL-BOUND-UNKNOWN-ROOT` / 鲜血药水 | `e6fe6a56234b45009e141cabb8b7e14d` | Passed；根认证为 false，真实用药与模拟差分通过，HP35→49 |
| `HEAL-BOUND-UNKNOWN-ROOT` / 炼金术 | `66b652b2541a4c6b830a0d5e53dc04eb` | Passed；无药水但含未认证生成牌时根认证为 false；只验证根与 Fork，不执行炼金术或声称生成路线通过 |

公共配置：SILENT、`FUZZY_WURM_CRAWLER_WEAK`、seed `SILENTRECOVERYBOUND20261001`、敌 HP999、玩家最大 HP70、清空当前牌堆、`-VerifyCombatRootSnapshot`。第一项玩家 HP50／能量20，手中四种升级基础牌及一张普通打击，抽牌堆含四种普通基础牌；后两项玩家 HP35。没有增量搜索或整场自动战斗。

鲜血药水夹具先注入一瓶供根认证，原生差分入口又注入并消耗测试瓶，因此动作阶段含两瓶，不能称单瓶场景。首轮未进入行为测试，因为启动器要求旧布局 `default/1/settings.save`；从实际 Steam 布局只读复制设置和进度到私有实例后启动成功，不修改玩家数据。随后第一次组合夹具的根认证通过，但防御／生存者错误使用 `Target=Player`，原版拒绝出牌；改为 `Target=None` 后取得上表最终证据。错误夹具结果保留于 `.local/silent-recovery-bound-20261001/fixture-invalid-target-result.json`，没有为此改动生产语义。

## 两个完整 Coordinator 对照

基线为行为源码 `512bd7c9`，后续 `eab8e20f` 只改文档。复用其已完成的 `.local/early-turn-incumbent-20261001/candidate-improvement` 与 `candidate-no-improvement`，没有重跑相同基线；本轮候选各运行一次独立普通 .NET 进程。

两侧均为 SILENT／`TERROR_EEL_ELITE`、A0／act0、Custom Beam96、单 solver 8,000 节点、card/pile/hand 分支32/18/24、DOP1、Smart、Coordinator、depth2、主搜／探索累计时限90,000 ms，外层120秒，组合／NoGC／增量关闭。根文本、live／continuation 戳、实际政策与预算逐字段一致；两侧都未命中时间边界。

| seed | 胜负／战损／药水／结束回合 | 请求展开 A→B | 请求转移 A→B | ETC 次数 A→B |
|---|---|---|---|---|
| `CSOPT20261001D` | 胜利／59／0／17，保持 | 85,950→77,409（−9.9%） | 247,414→220,089（−11.0%） | 10→10 |
| `CSOPT20261001` | 胜利／53／0／17，保持 | 62,229→56,485（−9.2%） | 181,831→163,899（−9.9%） | 8→8 |

第一根完整路线变化，首次探索改进由 depth2/rank1 提前至 depth1/rank0；仍各有一次严格改进，但因此扩展的层不同。第二根完整路线和所有保存的质量字段保持。不是只减少物理模拟成本的同工作量优化。

**评分尾键取舍必须单列：** 第一根 Score 从 `9998359931` 降至 `9998259931`；生产 Coordinator 比较器包含 Score 时判候选较差，去除 Score 尾键时 `materialComparison=0`。胜负、战略战损、药水、成长、死亡保护、结束回合及已保存终局摘要保持，不能把它写成完整排序质量不变或所有状态等价。此次保留这项窄扩展，结论限定为样本的主要战斗结果保持、工作量减少，后续更多根仍须关注该尾键与路线变化。

请求总搜索耗时记录为 30.362→23.525 秒、22.557→10.414 秒。基线是同源码既有样本，不是本轮交错测量；未做 ABBA 或可见 Steam 性能验收，不将这些墙钟变化写成稳定提速、FPS 或帧时间收益。

完整结果、根戳、质量、路线和生产比较器输出位于 `.local/silent-recovery-bound-20261001/`，其中 `comparison.json` 和 `quality-comparison.json` 保留对照口径。

## 构建、部署与限制

Release 构建 0 警告／0 错误（20.65秒），Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=238`。初次沙箱构建无法读取本机 NuGet 配置，授权读取环境后正常构建；不是源码编译错误。23:07:19 已用该最终源码产物精确覆盖确认的本地 Mod 与既有创意工坊安装副本：manifest、CombatSolver DLL、Windows MemoryCleaner、LICENSE、THIRD_PARTY_NOTICES.md。无头批次成功清理，部署时没有运行中的游戏。

没有测试正式多人入口、全原版内容、原生完整自动部署、Linux 或可见游戏性能，没有提升版本、打包或上传渠道。预算调整和自动保存开战检查点仍未实现。

## 后续日志归因字段（2026-10-02）

为后续实机检查认证是否实际命中，`COMBAT_ROOT_CAPTURE` 与 `TURN_SETUP_ROOT_CAPTURE` 都记录 `strategic_hp_recovery_bound=certified|bypassed`、稳定的首个拒绝原因码 `strategic_hp_recovery_bound_reason`、该原因对应的来源 ID `strategic_hp_recovery_bound_source`（URI 转义；无单一来源时为 `-`），以及 `strategic_hp_recovery_bound_postcombat_heal_hp`。认证通过时该值是固定战后治疗量；未通过时输出 `unbounded`。这些字段证明门禁结果，不证明这场战斗实际利用了更紧的界。首轮日志分析确认自动搜索入口还需补上根认证字段；随后遇到十次 `unsupported_relic`，旧日志没有遗物 ID，无法判断能否安全扩展白名单，因此增加来源 ID 字段。详情与日志覆盖量见[测试矩阵](../TEST_MATRIX.md#生命界认证与边际剪枝日志2026-10-02)。

搜索结果和早期回合续搜成员日志新增 `primary_incumbent_certified_healing_bound_pruned`／`incumbent_certified_healing_bound_pruned`。它只计当前认证上界判为可剪、但按完整缺血余量仍可保留的 incumbent 检查候选，属于边际归因；原有剪枝合计字段保持不变。计数不是减掉的总展开、墙钟收益，也不包含 Beam 保留等其他阶段。旧实机日志未含这些字段，因此只能由本次部署后的战斗验证实际命中。本轮源码差异检查与 Release 构建通过（0 警告／0 错误），五个部署文件已精确覆盖至 `D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\CombatSolver`。未运行游戏或战斗测试。

### 后续来源归因实施（2026-10-02）

最新实机里十个生命界根因 `unsupported_relic`，但日志没有具体遗物 ID。新增 `strategic_hp_recovery_bound_source`：当首个拒绝项来自特定角色、敌人、药水、遗物、玩家 Power 或卡牌时记录相应 ID；其他拒绝及认证成功记 `-`。两类根捕获入口都写此字段，值做 URI 转义。它只用于定位认证缺口，不更改认证与搜索。Release 构建 0 警告／0 错误；五个部署文件已复制到本地 Mod。无战斗测试或实机验证。

### 遭遇 Modifier 来源补充（2026-10-02）

带来源字段的下一局共33次根捕获，全部以 `encounter_modifier` 作为首个拒绝原因，因此 source 均为 `-`；94个 ETC 成员合计457,043节点、59.805秒，严格采用17次，生命界边际剪枝为0。为查明 Modifier 是否是可证明安全的来源，本次将首个 Modifier 的 `Id.Entry` 也写入同一来源字段。认证白名单及剪枝没有扩大；Release 构建0警告／0错误，五个文件已部署本地 Mod，未运行战斗测试；这项补充还需要新实机日志验证。
