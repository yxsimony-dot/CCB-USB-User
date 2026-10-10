# 保存 PIN 后成功与重启后失败：调用顺序对照

日期：2026-10-08。只读代码审计；本轮没有真实输入、令牌调用或生产修改。以下是已证实的代码差异，不是已确定根因。运行时厂商内部状态未读取。

## 保存按钮不会提前验证或登录

- `src/UsbWebLauncher.App/Presentation/LocalPinCommands.cs:79` 的 UI 命令虽名为 `ValidateAndSaveAsync`，最终在 `:133` 调用的是 `SaveFixedAsync`，不是 setup service 的同名验证方法。之前仅检查 broker health、显示本地设置窗口。
- `src/UsbWebLauncher.Core/Authentication/ClientCertificatePinSetupService.cs:42`：删除旧 stamp → 保存凭据 → 保存 pending stamp → 通知 `ReportPinProvisionedAsync`。这一方法不调用 unlocker/verifier。
- `src/UsbWebLauncher.App/Composition/WindowsClientCertificateAuthenticationRuntime.cs:46` 创建 `ManualAuthenticationGate`，`:47` 将它作为 setup lifecycle observer。它在 `ManualAuthenticationGate.cs:104`、`:118` 只记录 pending Provisioned 并要求显式启动，不立即通知 coordinator。
- 真正调用 unlocker/verifier 的另一个方法在 `ClientCertificatePinSetupService.cs:106`，当前设置 PIN 的 UI 不走它。因此没有发现“重新保存时先验证/预登录，重启漏掉”的代码路径。

## 下一次启动的两路顺序

| 环节 | 刚保存 PIN 后点击启动 | 重启后读取已有 PIN 并点击启动 |
| --- | --- | --- |
| manual gate | 消费 pending Provisioned，先 `ReportPinProvisionedAsync` | 新 gate 的 pending 为 None |
| coordinator | 清 `_pinReuseBlocked` / `_attempted` / active attempt，释放旧认证资源；若设备已存在就立即开始新 cycle | 初始资源为空；首次 device-present 开始新 cycle |
| 存储 | 读取刚保存的 pending stamp 和凭据 | 读取已有 pending/validated stamp 和凭据 |
| CSP | 两路都进入同一 `StartCycleAsync` / `UnlockAsync` | 相同 |
| 浏览器 | 若本进程先前已初始化，复用现有 browser host / WebView 环境 | 新建 runtime/browser host，首次初始化 |

准确位置：`ManualAuthenticationGate.cs:74` 消费通知；`AuthenticationCoordinator.cs:470` 处理 provisioned；`:90` 为首次存在启动；`:525` 为共同 `StartCycleAsync`；`:755` 为资源释放。

共同 CSP 顺序在 `src/UsbWebLauncher.Windows/Authentication/WindowsCryptoApiTokenUnlocker.cs:125`：`CryptAcquireContext(CRYPT_SILENT)` → `CryptSetProvParam(PP_SIGNATURE_PIN)` → `CryptGetUserKey`。`:141` 明确 continuation 启用时跳过签名 challenge `TryProbe`，两路一致。成功持有 provider/key session；`:492` 的 Dispose 销毁 key 并释放 provider，没有显式 logout 操作。不能据此推断厂商内部缓存必然清空或保留。

pending stamp 仅让 coordinator 在解锁前发布 `ValidatingCompatibility` 状态，并在 TLS 成功后写 validated stamp（`AuthenticationCoordinator.cs:168` 起）。它不修改 CSP 调用参数。`WindowsNativePromptContinuationClient.cs:210` 附近的 Arm 数据仅包含 policy ID、认证 hash、attempt ID、PIN，不包含 pending/validated 标记或厂商 encoder 参数。

## 仍需区分的会话状态

`src/UsbWebLauncher.App/Composition/ManualLauncherActions.cs:97` 在首次启动走 launcher Start，后续走 Retry/Resume。`src/UsbWebLauncher.Core/Coordination/LauncherCoordinator.cs:479` 在 `_browserInitialized=true` 时跳过 `InitializeAsync`。保存 PIN 不销毁 browser host、不重建 WebView、不清厂商 DLL 的进程静态状态。因此同一进程再次启动和退出 EXE 后首次启动不是同一环境；这才是尚未排除的生命周期差异。设置窗口中用户实际键入，也会经过 Windows 键盘/输入法状态，但设置代码没有主动设置 Caps/Shift/IME；不能把这点认定为根因。

厂商静态分析已见：EDIT 构造函数把调用方参数指针存到对象 `+E8`；父 dialog 将传入参数透传；调用点来源为外层对象 `+C8` 或栈缓冲；焦点建立时复制 8 字节到全局 hook 对象。当前证据没有证明这些参数由一次预登录产生，亦未建立它与本地 SaveFixed 的调用关系。参见 `artifacts/test-tools/native-executor-probe/vendor-character-path-notes.md:42`。错误参数可能影响后续认证，但不是已观察到的根因。

## 最小结论

已排除代码层面的“保存先验证、重启少一步验证”解释。已识别真实差异是旧 session 的释放/重建、pending 通知与 stamp、以及浏览器/厂商进程状态复用。之前 exact-PIN 存储与重建测试通过，但那不验证厂商安全控件收到的字符。回滚恢复能输入后，应以银行最终是否接受为结果边界；broker `Success/Completed` 只证明发送/提交路径完成，不等于银行认证通过。

提升权限 synthetic fixture 仅留 artifact，未运行，不作为本报告证据。
