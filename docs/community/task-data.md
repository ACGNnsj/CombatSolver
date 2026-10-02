# 社区任务资料

任务入口在[贡献指南](../../CONTRIBUTING.md)，具体实验入口见[夹具与开发脚手架](testing-guide.md)。

## 首批范围

2026-10-02 固定日志后台未修复报告 ID，取 CombatSolver **0.44.0 起**，包含 0.44.0，共 2553 份报告、2383 个战斗 session。快照之后的新报告进入后续批次。

322 份报告带 `BetterWorldline` 信号，按 session 合并成 312 个候选。排序使用报告预测 `hpLoss.before − hpLoss.after`，缺失值单独保留。首批发布前十个，预测差为 31～76 HP；该数值没有通过本轮整场回放验证。

其余信号和重算计数进入诊断清单。一份包可以同时属于故障和优化队列；完整索引保留关联。首批选择十个具体异常或定位任务，优先有 0.47.3 记录、边界和日志明确的签名。纯保护停止、手操偏离、原因未知与第三方支持请求各保留自己的分类。

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

资料存放在独立 [community-tasks-2026-10-02 Release](https://github.com/Torch1230/CombatSolver/releases/tag/community-tasks-2026-10-02)，Release 正文链接任务总表。`B01.zip`～`B10.zip` 是故障任务材料，`Q01.zip`～`Q10.zip` 是优化材料；解压外层后得到代表报告 ZIP，将报告 ZIP 交给回放入口。

`community-task-index.json` 保存完整范围内的报告归属、诊断组、优化候选与首批任务；`optimization-ranking.csv` 可直接查看降序清单。已有 GitHub 任务用批次任务 ID 关联，后续发布时沿用 ID 检查既有 issue，保持认领与讨论。

[export-community-bundle.py](../../tools/export-community-bundle.py) 生成公开副本，清理 `report.json` 和 `diagnostics/` 中的昵称、联系方式、玩家统计字段及个人路径。**`replay/*` 保留原字节**，用于保存牌序、RNG、模型身份、原生状态与录制事件；首批材料的单人玩家 `net_id` 均为游戏测试身份 1。公开副本的 ZIP 字节与原包不同，原包保留在后台。

```sh
python tools/export-community-bundle.py .local/raw/REPORT.zip .local/public/REPORT.zip
```

导出前拒绝路径穿越、链接、加密和游戏程序集；静态核对回放条目与原包一致。导出检查只证明资料处理范围，实际可恢复性由认领者运行工具判断。

## 处理记录

认领、首因、夹具和验证证据放 issue/PR。修复 PR 完整通过验收后关闭子任务；范围外报告继续留在清单，后台归档依据报告 ID 和实际修复证据处理。当前发布任务没有将后台报告标记为已修复，也没有生成客户端新版本。
