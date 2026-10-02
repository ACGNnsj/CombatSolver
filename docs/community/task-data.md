# 社区任务资料

任务入口在[贡献指南](../../CONTRIBUTING.md)，具体实验入口见[夹具与开发脚手架](testing-guide.md)。

## 当前范围与历史素材

当前只取 **CombatSolver 0.47.x** 未修复报告。历史快照中该版本段有 1138 份报告、1065 个战斗 session。老版本先不选；固定报告 ID 后再静态分析，后续新报告进入下一次筛选。

完整历史快照曾包含 0.44.0 起的 2553 份报告，以及 session 去重后的 312 个优化候选，作为历史元数据保留。当前优化代表均为 0.47.x，五个遭遇主题的预测战损差为 45～76 HP，实际收益未验证。

当前为 **五个批次、25 个主题、33 个代表包**。故障 B012～B015 共 20 个机制主题；优化 Q002 包含五个具体遭遇主题。**每批五主题，每主题一至两包，一个 issue，一次认领整批。** 卡牌名和报告 ID 是样本信息，验收按主题组织。

社区任务面向原版游戏内容，主项目不主动适配修改游戏内容的第三方 Mod。角色筛选之外，还检查最早异常栈。原版牌进入 RebalancedSpire 内容补丁的错误已移出；外部补丁归属不清和证据不足的样本也可以跳过。

## 去重口径与验证状态

- 报告/session 去重后，继续读取最早异常、第一处状态分叉和源码，按所有权、执行阶段及共同调用链归并主题。
- 卡名、回合号、牌堆位置和包装异常不能作为拆主题依据。选牌回放、路线材料化、原生选择和回合准备等共享入口先按机制调查，不逐卡派发。
- 同报错行不证明同根因。证据分为静态因果已定位、共同机制首因待证、证据不足；最后一类直接跳过。不同已证实首因可在主题内补充子问题。
- 发布前完成静态读取与资料整理，恢复、搜索和部署均未执行；旧问题在当前代码是否仍存在尚未验证。
- 默认一个主题一包；第二包只补充不同调用链或关键边界。重复样本舍弃，允许漏掉，不追求全量分发。
- 后续检索到已有主题，直接删除该报告服务器 ZIP 并跳过，不补材料、不重新发布。修复状态不因发布或丢弃重复而改变。

分类工具为 [classify-community-reports.py](../../tools/classify-community-reports.py)，接受维护者导出的 JSON 快照，输出诊断组、优化排名和每份报告归属：

```sh
python tools/classify-community-reports.py --reports .local/community-tasks/reports.private.json --output .local/community-tasks/classification.private.json
```

基础分类工具仅生成症状桶，不证明共同根因。发布选择用 [select-community-diagnostics.py](../../tools/select-community-diagnostics.py)，默认过滤 0.47.x，并读取[主题登记表](theme-registry.json)。已有主题输出固定报告 ID、删除原因及 GitHub 代表材料回执；其余候选继续静态分析。泛型异常匹配需结合调用方证据，避免把所有空引用或集合错误视为同一原因。

## 下载和公开副本

资料存放在独立 [community-tasks-2026-10-02 Release](https://github.com/Torch1230/CombatSolver/releases/tag/community-tasks-2026-10-02)，Release 正文提供各批次议题与整批下载：

- [B012：T001～T005](https://github.com/Torch1230/CombatSolver/issues/149)，9 个代表包。
- [B013：T006～T010](https://github.com/Torch1230/CombatSolver/issues/172)，8 个代表包。
- [B014：T011～T015](https://github.com/Torch1230/CombatSolver/issues/173)，6 个代表包。
- [B015：T016～T020](https://github.com/Torch1230/CombatSolver/issues/174)，5 个代表包。
- [Q002：O001～O005](https://github.com/Torch1230/CombatSolver/issues/150)，5 个代表包。

批次 ZIP 按主题编号分目录，包含 `theme.json`、`static-evidence.json` 和 `reports/*.zip`。把代表报告 ZIP 交给回放入口。旧十项批次已经迁移，关闭只表示归并；旧 ZIP 和排名 CSV 保留历史用途，当前认领以五主题批次为准。

`community-task-index.json` 保存当前 0.47.x 报告归属、主题、代表和批次，也保留历史条目及迁移状态。`theme-registry.json` 是稳定机制、匹配规则、证据等级和已发布资料去向的维护入口。认领单位仍为整批，PR 按主题编号记录进展。

[export-community-bundle.py](../../tools/export-community-bundle.py) 生成公开副本，清理 `report.json` 和 `diagnostics/` 中的昵称、联系方式、玩家统计字段及个人路径。**`replay/*` 保留原字节**，用于保存牌序、RNG、模型身份、原生状态与录制事件；发布材料静态检查中的单人玩家 `net_id` 均为游戏测试身份 1。公开副本的 ZIP 字节与原包不同。

```sh
python tools/export-community-bundle.py .local/raw/REPORT.zip .local/public/REPORT.zip
```

导出前拒绝路径穿越、链接、加密和游戏程序集；静态核对回放条目与原包一致。导出检查只证明资料处理范围，实际可恢复性由认领者运行工具判断。

## 处理记录

每批由一名 Assignee 负责全部五个主题。首因、最小夹具和实际验证放 issue/PR，按主题登记验收；五主题全部完成后关闭批次。交接时整理已完成/未解决主题及证据，维护者调整 Assignee。

发布流程固化在 [combatsolver-community-tasks skill](../../.agents/skills/combatsolver-community-tasks/SKILL.md)。清理有两个时点：GitHub 发布成功后清理实际代表包，或者检索确认已有主题时直接丢弃重复包。只删固定 ID 对应磁盘/COS ZIP，报告、旧备注和修复状态保留，追加原因与 GitHub 去向。本次代表复用已发布且服务器已清理的资料，另按新主题清理了 342 份 0.47.x 重复报告的服务器 ZIP。

[publication-ledger.json](publication-ledger.json) 保存当前主题批次、历史迁移和清理回执。清理工具 [retire-community-archives.py](../../tools/retire-community-archives.py) 在日志服务容器读取固定清单，支持 dry-run；`published` 表示已发布代表清理，`duplicate_theme` 表示重复主题删除跳过，后者还校验真实报告版本属于 0.47.x。
