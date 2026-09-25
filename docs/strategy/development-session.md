# 常驻策略迭代会话

此入口仅供本地问题包排查。第一次 `start` 启动并保留一个隔离游戏进程；后续 `run` 逐包提交请求，原生 DLL 和游戏依赖不变时复用同一 PID。启动器发现主 DLL 或依赖变化会重建私有快照并重新启动。`stop` 按进程身份停止并清理该实例。

`start` 默认打开独立只读监控窗口；游戏仍以 headless 运行。窗口每秒刷新一次，显示包、请求、PID、脚本与参数版本、阶段、时限、节点、速率、前沿、当前最好预计战损与用药，以及错误或超时原因。关闭窗口不会停止游戏；在仍运行的会话上再次执行 `start` 可重开窗口。`start --no-monitor` 关闭该会话的监控，供批量或性能对照使用。Windows 使用独立 WPF 窗口；Linux 使用可用的终端模拟器与同一状态文件，本轮未运行 Linux 门禁。

Windows 示例（在仓库根目录执行）：

```powershell
pwsh -NoProfile -File tools/strategy-session.ps1 start ability-work --game-root 'D:\Steam\steamapps\common\Slay the Spire 2' --ritsu-root 'D:\Steam\steamapps\workshop\content\2868840\3747602295'
pwsh -NoProfile -File tools/strategy-session.ps1 run ability-work '.local/issue-bundles/strategy-0923/raw/<报告 ID>.zip' --script 'tools/strategy-example.cs' --params '.local/strategy-parameters.json'
pwsh -NoProfile -File tools/strategy-session.ps1 status ability-work
pwsh -NoProfile -File tools/strategy-session.ps1 stop ability-work
```

Linux 入口为 `tools/strategy-session.sh`，命令和选项相同。这里保留跨平台入口；本轮没有运行 Linux 门禁。

脚本是普通 C# 源文件，实现公开的 `IDevelopmentSearchStrategy`；编译只作用于这个脚本项目。参数文件是数值 JSON 对象，例如 `{"persistentBuffWeight": 2}`。`run` 在提交请求前复制脚本和参数，并按脚本内容及主 DLL 身份缓存编译结果。下一次请求读取新文件；已开始的搜索沿用自己的版本。脚本实现可以通过四个钩子调整候选优先级、中途评分、一个有界保路代表及现有搜索组合成员的编排，也可以在脚本中组合只读特征生成新的估值维度。新游戏状态或新模拟原语仍需扩展主程序；最终胜负、战损和资源排序不交给脚本。

默认 `VeryHigh`、180 秒、DOP 8。到时的包记 `timeout` 和 `exceeded_180_seconds_package_discarded`，跳到下一包；不把超时结果写成路线收益。每次结果在 `.local/strategy-sessions/<会话>/requests/<请求>/session-result.json`，含原包、模式、主 DLL 哈希、脚本和参数哈希、PID、复用标记、墙钟与求解指标；总索引为 `results.jsonl`。编译失败留 `script-build.log`，请求记 `strategy_or_input_failed`；游戏内脚本异常由无人请求写入 Failed，不使用旧结果。

开发会话 `run` 固定 `SearchOnly`；`--selector` 默认 `start`，搜索质量比较须保持 `combat_start` 同根。`--policy` 可传已有的回放政策覆盖文件。`status` 只报告当前私有进程是否存活；`run` 的启动器继续核对进程出生时间、游戏可执行文件与冻结依赖身份。
