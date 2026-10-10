# 实施与验收记录（2026-10-09）

## 2026-10-10 拔出 Key 自动整轮中止补充

用户指出旧版物理拔出后仍保留网页并进入“继续业务”状态。根因为旧协调器将拔插当作同会话设备周期，未调用整轮中止。现增加应用层 SessionUsbDeviceMonitor：绑定本轮首次识别的物理设备，成功快照确认原设备消失时，在旧协调器处理替换设备前同步撤销会话，并投递与手动中止相同的完整清理。结束后的快照不再进入旧协调器；下一次明确启动创建新监控和浏览器。旧页重试也校验会话有效性。

同一物理设备仍在但元数据缺失、枚举错误、无关设备移除、首次无 Key 等待均不误判。物理拔出判定以 Windows 实际报告的成功快照为准，若极短同 ID 拔插完全被系统合并而无任何消失快照，不能从持续存在的快照推断拔出。

回归 9/9（含旧认证仍在等待时立即撤销），完整 .NET 1462/1462、网页 265/265 通过，Release 发布成功。证据目录 `artifacts/verification/key-removal-session-20261010/`，红绿证据 `artifacts/verification/stop-on-key-removal-20261010/`。

已备份并更新原入口，App DLL SHA256 `00EB80EAEA94F8C98D018D367869F46F974493DBDD82A8674B6CBE0C43B93EAA`，认证绑定安装退出码 0；打开 PID 37808。任务 6.1 完成。

本轮实机已记录一次成功自动登录，随后实际拔出触发 `session-key-removed` → `session-stopped`，清理 99 ms，恢复启动按钮、公司清空、进程保持响应。用户确认已拔插后再次核实仍保持初始状态，拔出之后新启动数为 0。见 `physical-removal.json` 及脱敏日志。

之后明确点击启动建立 session 2，同 PID 37808 完成新的原生认证（Success/Completed）、一次网页登录提交和当前公司识别，按钮为可用的“中止业务”。见 `reinsert-restart.json`、`restart-authentication.txt`、`restart-lifecycle.jsonl`。任务 6.2 实物验收完成，程序保留在本轮已登录页面，便于用户继续使用。以下是前版本的验收历史。

## 已发布行为

- 初始显示“启动业务”；受理启动即变为可用的“中止业务”，不受认证、查询、导出的忙状态限制。
- 中止撤销本轮页面及原生认证授权，取消导出，释放浏览器主/下载控制器，清除公司与业务提示，再恢复初始空白界面。
- 下一轮显式启动使用新 WebView2 控件和独立数据目录；旧轮回调被会话代次和取消信号隔离。
- PIN、目录、月份及已发布 PDF 保留；未完成下载取消且不继续回单阶段。
- 中止普通异步清理限 7.5 秒；UI 可响应时超时进入失败状态。中止和关闭各自有独立八秒看门狗，原生同步卡死时仅退出本程序。

## 原生崩溃定位与修复

真实登录后，旧的逐帧事件解绑在 `CoreWebView2Frame.FrameCreated -= ...` 处触发 `EmbeddedBrowserWebView.dll` 的 `0x80000003`。逐步诊断记录显示前一步 `frame-web-message` 完成，但 `frame-created` 未返回。

修复为销毁时仅撤销托管帧登记及回调，让 WebView2 主控制器统一回收原生帧；Destroyed 回调捕获原始帧，不依赖可能不同的 sender 包装对象，也不调用已销毁帧的原生解绑。

另修复 UI 释放器同步重入、初始化与关闭竞争、页面尝试未链接会话取消及证书 deferral 未传递取消信号的问题。

## 自动化证据

证据主目录：`artifacts/verification/restartable-session-20261009/`。

- `release-final.trx` / `dotnet-release-final.txt`：1453/1453 通过。
- `browser-final.txt`：265/265 通过。
- `publish-final.txt`：Release win-x64 publish 成功。
- 新增回归覆盖忙时中止、重复请求、迟到回调、取消后禁止输入、原生 deferral 完成、浏览器唯一目录与安全回收、释放器重入。
- `lifecycle-log-validation.json`：86 条实际生命周期日志，仅允许固定阶段、编号、耗时、超时标记，不含凭据或业务正文。
- `artifacts/verification/restartable-business-session-20261009/`：停止/关闭子进程故障注入、迟到初始化、下载完成/取消竞争及文件保留证据。

## 最终版实机结果

| 场景 | 结果 |
| --- | --- |
| 同 Key 第一次登录后中止 | 147 ms 返回启动按钮，公司清空 |
| 同 Key 第二次登录后中止 | 256 ms 返回启动按钮，公司清空 |
| 中止后关闭 | 291 ms，退出码 0 |
| 已登录时直接关闭 | 620 ms，退出码 0 |
| 登录开始后中止 | 73 ms，未继续提交登录 |
| 导出已开始时中止 | 129 ms，恢复初始状态 |
| 文件与设置保留 | 原有 2 份 PDF 哈希一致，下载设置哈希一致；固定 PIN 自动登录成功 |

上述实机结果见 `live-cycle.json`、`live-close.json`、`live-stop-login.json`、`live-stop-export.json` 及对应脱敏 `.jsonl`。导出中止测试在查询阶段取消，没有额外下载文件；完成下载与取消竞争另由真实下载对象回归验证。

发布入口：`artifacts/publish/win-x64-manual-actions-20260930/UsbWebLauncher.App.exe`。

App DLL SHA256：`3B2742C082CC78D1B4393DF9C380F7E7F7E59BAB7E90A3E5CB50B46A56747EA7`。正式与 staging 的 App/Core/Windows 三程序集哈希一致；已备份旧文件，认证组件安装退出码 0。未覆盖配置或凭据。见 `deployment-final.json`、`broker-install-final.json`、`final-open.json`。

## 换 Key 实机验收（2026-10-10 完成）

用户已实际换插另一只 Key。原 PID 57000 保持启动按钮及公司占位，插拔没有自动启动，见 `key-swap-before.json`。首次显式启动后匹配到“验证通用盾密码”，候选及匹配数均为 1，但两次测试均在 `ForegroundWait` 返回 `FocusLost`，没有进入 PIN 输入阶段。此时 Windows 前台进程为 SearchApp；测试侧显式激活 Launcher 亦未取得前台，因此不能将失败归因于 Key 匹配或错误 PIN。

首次失败后中止 183 ms 返回初始状态，见 `key-swap-first-stop.json`。厂商外部密码窗口不随网页销毁关闭，测试侧仅取消了本轮确认过且尚未输入的窗口，未终止厂商进程。第二轮亦已中止，恢复初始界面。两次脱敏认证状态见 `key-swap-first-focus-status.txt`、`key-swap-second-focus-status.txt`。

2026-10-10 用户手动点击“启动业务”后，原 PID 57000 的会话 3 完成新 Key 认证：原生认证 `Success/Completed`，本轮网页登录恰好一次 `Submitted`，公司显示从占位变为实际内容，中止按钮可用。未重启应用，未更改 PIN 或认证代码。见 `key-swap-login-success.json`、`key-swap-success-status.txt`。

随后中止成功，公司清空并恢复可用启动按钮，应用保持响应且仍为原 PID。UI 自动化观测到恢复耗时 2127 ms（应用自身 session-stopped 日志记录清理和重建为 106 ms）；原有 2 份 PDF 及下载设置哈希均未变化。见 `key-swap-final-stop.json`、`key-swap-lifecycle.jsonl`。更新后的生命周期脱敏校验覆盖 153 条事件。

任务 5.3 已完成，最终版保持打开在初始界面。本轮确认正常前台环境下换 Key 可以重新自动认证及登录；此前 SearchApp 占据前台导致的焦点保护仍存在，不将本轮通过描述为修复了 Windows 前台限制。
