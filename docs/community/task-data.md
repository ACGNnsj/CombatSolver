# 社区任务资料

任务入口在[贡献指南](../../CONTRIBUTING.md)，具体实验入口见[夹具与开发脚手架](testing-guide.md)。

## 首批范围

2026-10-02 固定日志后台未修复报告 ID，取 CombatSolver **0.44.0 起**，包含 0.44.0，共 2553 份报告、2383 个战斗 session。快照之后的新报告进入后续批次。

322 份报告带 `BetterWorldline` 信号，按 session 合并成 312 个候选。排序使用报告预测 `hpLoss.before − hpLoss.after`，缺失值单独保留。首批发布前十个，预测差为 31～76 HP；该数值没有通过本轮整场回放验证。

其余信号和重算计数进入诊断清单。一份包可以同时属于故障和优化队列；完整索引保留关联。故障队列为 B001～B011，共 110 项；优化 Q001 包含排名前十个候选。**每批 10 项共用一个 issue，一次认领整批**。纯保护停止、手操偏离、原因未知与第三方支持请求各保留自己的分类。

社区任务面向原版游戏内容，主项目不主动适配修改游戏内容的第三方 Mod。已知第三方类型/Hook 缺失、第三方角色及依赖第三方内容的场景留档，排除认领队列；有原版内容证据的通用异常才发布。B001 的原 B09 适配请求已移出，B09 使用原版故障条目替换，历史材料保留。

## 去重口径与验证状态

- 报告 ID 去重；session 相同的报告保留关联，优化排名每场只取一个代表。
- 相同异常文本归入诊断桶，同一异常挂多个信号时合并。归一化去除临时 ID、牌堆位置和回合编号，保留异常、动作类型、模型和差异字段等线索。
- 多份同签名报告的数量表示该线索的出现范围。相似症状可能有多个根因；认领者依据日志、源码和最小验证继续拆分。
- 发布前完成静态读取与资料整理，恢复、搜索和部署均未执行；旧问题在当前代码是否仍存在尚未验证。
- 任务的“代表包”可直接下载，其余关联报告 ID 留在索引；当前批次只上传首批所需代表材料。

分类工具为 [classify-community-reports.py](../../tools/classify-community-reports.py)，接受维护者导出的 JSON 快照，输出诊断组、优化排名和每份报告归属：

```sh
python tools/classify-community-reports.py --reports .local/community-tasks/reports.private.json --output .local/community-tasks/classification.private.json
```

输入字段：`id`、`receivedAt`、`modVersion`、`gameVersion`、`combat`、`hpLoss`、`issues`、`unexpectedReplans`、`archive`，以及可选 `classification`。范围外版本显式报错。工具只读本地快照，不访问或修改后台。

## 下载和公开副本

资料存放在独立 [community-tasks-2026-10-02 Release](https://github.com/Torch1230/CombatSolver/releases/tag/community-tasks-2026-10-02)，Release 正文提供各批次议题与整批下载：

- [故障批次 B001：B01～B10](https://github.com/Torch1230/CombatSolver/issues/149)，下载 `B001.zip`。
- 故障批次 B002～B011：100 项新增定位任务，议题与整批材料链接见[社区入口](https://github.com/Torch1230/CombatSolver/issues/171)。
- [优化批次 Q001：Q01～Q10](https://github.com/Torch1230/CombatSolver/issues/150)，下载 `Q001.zip`。

批次 ZIP 内按条目编号分目录，目录里的 `reports/*.zip` 是代表报告。把代表报告 ZIP 交给回放入口。原单项议题已按合并归档关闭，关闭只表示移入批次；材料与历史链接保留。

`community-task-index.json` 保存完整报告归属、诊断组、优化候选和批次；每个条目的 GitHub 链接指向批次正文对应编号，旧单项链接作为历史记录保留。`optimization-ranking.csv` 可直接查看降序清单。认领单位是整批，PR 关联批次 issue，条目编号用于进度与验证记录。

[export-community-bundle.py](../../tools/export-community-bundle.py) 生成公开副本，清理 `report.json` 和 `diagnostics/` 中的昵称、联系方式、玩家统计字段及个人路径。**`replay/*` 保留原字节**，用于保存牌序、RNG、模型身份、原生状态与录制事件；发布材料静态检查中的单人玩家 `net_id` 均为游戏测试身份 1。公开副本的 ZIP 字节与原包不同。

```sh
python tools/export-community-bundle.py .local/raw/REPORT.zip .local/public/REPORT.zip
```

导出前拒绝路径穿越、链接、加密和游戏程序集；静态核对回放条目与原包一致。导出检查只证明资料处理范围，实际可恢复性由认领者运行工具判断。

## 处理记录

每批由一名 Assignee 认领并负责全部 10 项。认领、首因、夹具和验证证据放批次 issue/PR，清单逐项记录状态和 PR。完成一个条目时更新该行；十项全部验收后关闭批次。需要交接时，由原负责人整理已完成项、未解决项与证据，再由维护者调整批次 Assignee。范围外报告继续留在清单，后台归档依据报告 ID 和实际修复证据处理。

发布流程固化在 [combatsolver-community-tasks skill](../../.agents/skills/combatsolver-community-tasks/SKILL.md)。发布与修复分别记录：确认 GitHub 上传和批次创建成功后，只清理实际发布代表 ID 对应的服务器磁盘/COS ZIP 与本地暂存，报告行、诊断索引、旧备注和未修复状态保留，并追加 GitHub 材料去向。关联但未上传的报告包继续保留。

[publication-ledger.json](publication-ledger.json) 保存批次、诊断组、代表 ID、GitHub issue/附件回执与逐包清理结果；完整资料索引在 Release。清理工具为 [retire-community-archives.py](../../tools/retire-community-archives.py)，在日志服务容器中读取发布回执，支持 dry-run。后台 archive 不可用时，从对应 GitHub 批次下载材料。
