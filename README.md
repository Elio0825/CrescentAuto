# 新月岛自动重进

Dalamud API 15 私有插件。按副本剩余时间或实例区域人数自动退出并重新进入新月岛，支持自动确认任务出发。

作者：Los

Dalamud 第三方仓库地址：

```text
https://raw.githubusercontent.com/Elio0825/CrescentAuto/main/pluginmaster.json
```

## 功能

- 通过 `ContentMemberList` Agent 请求刷新后读取 `InfoProxyContentMember.Instance()->EntryCount`，显示服务端实例成员列表的区域人数，不使用附近对象表。
- 读取 `EventFramework.Instance()->GetContentDirector()->ContentTimeLeft`，显示副本剩余时间。
- 副本剩余时间低于阈值、区域人数持续低于阈值或任一条件满足时自动重进。
- 自动点击 `ContentsFinderConfirm` 的出发按钮，带 750 毫秒重复点击节流。
- 通过 DailyRoutines 正式 IPC 查询并自动启用进出岛所需模块，不引用或打包 DailyRoutines 程序集。
- 可在进入南岛或北岛 10 秒后批量执行多个自定义插件命令；内置并默认启用“非法模式”（实际命令 `/bocchiillegal on`）。失败命令每 2 秒单独重试，30 秒后只提示失败，不中断主流程。
- 退出前等待战斗、咏唱、占用、过场和读图状态结束，并通过区域 ID 确认进出结果。
- 人数或时间读取失败时显示“未知”，不会按 0 触发自动退出。

## 区域与命令

| 区域 | TerritoryType | ContentFinderCondition | 进入命令 |
| --- | ---: | ---: | --- |
| 南岛 | 1252 | 1018 | `/pdrfe ocs` |
| 北岛 | 1346 | 1093 | `/pdrfe ocn` |

即刻退本命令：`/pdr leaveduty`

区域人数、剩余时间和出发确认已经内置，不依赖 DailyRoutines。进岛仍使用 `FieldEntryCommand`，退本仍使用对应的即刻退本命令；这样不需要复制 DailyRoutines 的跨区移动、传送、事件封包和任务队列，也不会把 `DailyRoutines.dll` 或 `OmenTools.dll` 打入插件包。

插件通过以下 DailyRoutines IPC 管理所需模块：

- `DailyRoutines.IsModuleEnabled`
- `DailyRoutines.LoadModule`

默认自动启用 `AutoTalkSkip`、`FieldEntryCommand` 和 `InstantLeaveDuty`，并将修改保存到 DailyRoutines 配置。模块加载是异步的，自动流程每 2 秒复查一次，最多等待 15 秒，等待期间不计入进出岛重试次数。`InstantLeaveDuty` 可能受 DailyRoutines 权限控制；加载失败时面板会显示具体原因。

每次由用户手动启动自动运行时，插件都会主动执行一次 DailyRoutines 模块检查与启用流程，并等待异步加载完成。

DailyRoutines 插件本体仍必须在 Dalamud 中启用。Dalamud 没有面向普通插件开放可靠的“自动启用另一个插件本体”接口；本插件只能在 DailyRoutines 本体运行后自动启用其内部模块。

## 默认配置

| 设置 | 默认值 |
| --- | ---: |
| 剩余时间触发 | 低于 100 分钟 |
| 区域人数触发 | 低于 20 人并持续 45 秒 |
| 进入后保护期 | 3 分钟 |
| 安全状态持续 | 10 秒 |
| 岛外等待 | 15 秒 |
| 失败重试 | 999 次 |

控制面板分为三个标签页：

- `运行设置`：目标区域、触发条件、自动功能开关和进岛后命令列表。
- `详细设置`：采样间隔、保护期、安全等待、岛外等待、进出确认超时、重试次数和兼容命令。
- `功能测试`：各项独立测试与 Dry Run。

运行设置中，“进岛后自动开启非法模式”与“进岛后执行自定义宏命令”是两个独立开关，非法模式开关显示在自定义宏命令上方。两类命令均在进岛 10 秒后执行。

自定义宏命令最多可配置 19 条，每条可独立启用和增删；加上不可删除的内置非法模式，总计最多 20 条。同一条重复命令在一次进岛中只执行一次。

## 构建

```powershell
dotnet build -c Release
```

国服 `XIVLauncherCN` 的默认 Dalamud 开发目录会被工程自动识别。其他安装方式可以先设置 `DALAMUD_HOME`，再执行构建。

构建产物：

- 开发插件 DLL：`bin\Release\CrescentAuto.dll`
- 打包文件：`bin\Release\CrescentAuto\latest.zip`

将 DLL 的完整路径加入 Dalamud 的开发插件位置并加载。确认 DailyRoutines 插件本体已启用，然后运行 `/crescentauto` 打开控制面板。所需内部模块可由本插件自动启用。

## 测试按钮

控制面板提供以下独立测试：

- 读取区域人数
- 读取剩余时间
- 确认当前出发窗口
- 进入南岛
- 进入北岛
- 即刻退本
- 完整重进流程
- 检查/启用 DailyRoutines 模块
- 执行全部进岛命令

每次测试会在面板和聊天框显示成功、处理中或失败原因。模块第一次被请求启用时会显示“处理中”，稍后再次测试即可确认最终状态。`Dry Run` 会阻止进岛、退本和完整流程发送命令；读取与确认当前出发窗口仍可直接测试。

## 命令

| 命令 | 作用 |
| --- | --- |
| `/crescentauto` | 打开控制面板 |
| `/crescentauto start` | 启动自动运行 |
| `/crescentauto stop` | 停止自动运行 |
| `/crescentauto abort` | 紧急停止 |
| `/crescentauto now` | 安全状态满足后立即退出并重进 |
| `/crescentauto enter` | 在岛外立即进入目标区域 |
| `/crescentauto status` | 输出区域人数、剩余时间和自动状态 |

进入后默认保留 3 分钟保护期，避免 `ContentTimeLeft` 在副本开场倒计时阶段被误判为即将结束。自动运行状态只保存在内存中；重新登录、插件重载或直接结束游戏后始终保持停止，必须由用户重新启动。
