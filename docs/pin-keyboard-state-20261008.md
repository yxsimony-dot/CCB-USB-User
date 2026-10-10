# PIN 输入大小写与键盘钩子时序调查

## 11:02 再次报错

用户确认重新设置 PIN 后曾全程自动登录成功；重启后仍报错。最新日志显示 IME 已从开启变为关闭，输入长度达到预期，单次确认执行完成，随后认证超时。因此前面的输入法处理没有解决根本的密码拒绝。现场非秘密日志保存于 `artifacts/verification/pin-still-rejected-20261008-1102`。

本轮没有提交真实密码，也没有让用户反复登录试验。此前本机凭据重读、重建认证协调器、协议缓冲所有权与 CryptoAPI 参数检查均未发现字符变化；自动测试只能排除其覆盖范围内的问题。

## 实际控件的实现证据

只读检查当前银行进程使用的 `C:\Windows\System32\D4Pinpad_CCB.dll`，SHA-256 为 `AE52F6FA9AA88EA51065AD5CC4D5AE14FD40510FF687ACD7C3B22542A611C285`。未修改、加载或调用该 DLL。

- RVA 24CE8 注册 `WH_KEYBOARD_LL`（类型 13），回调从 24670 跳到 24300。
- 字符处理读取 `KBDLLHOOKSTRUCT` 的虚拟键与 flags，使用 `GetKeyState(VK_CAPITAL)` 和 `GetKeyState(VK_SHIFT)` 选择字符映射。
- 首次注册路径由 MFC `WM_SETFOCUS` 消息处理函数进入，因此首次 hook 安装发生在安全输入窗口的线程。
- hook 对象包含进程共享状态；多窗口、多 UI 线程复用 hook 的情况不能仅据此证明其所有行为。

## 同控制点的隔离复现

`artifacts/test-tools/native-key-state-probe` 只创建自己的 EDIT 和低级键盘 hook，只捕获带自己标记的合成输入。真实用户键盘事件不记录、不拦截，finally 卸载 hook 并清理键盘状态。

用与实际控件一致的 `WH_KEYBOARD_LL + GetKeyState` 映射方式：整串一次 `SendInput` 两轮都出现大小写变化，显示仍为 8 个星号，长度检查仍通过。逐事件输入并等待处理后，合成字符正确。

进一步用固定 OS 工作线程、一次 `AttachThreadInput` 贯穿输入：Shift 事件发出后读取共享队列键状态，确认生效后才发字符；字符长度逐个确认后再释放 Shift，并等待松开状态。测试目标窗口忙碌 0 / 100 / 300 毫秒均得到准确合成字符，没有依赖固定 30ms 延时，也没有死锁或遗留 Shift。

这一结果证明现有整串模拟输入与实际控件使用的键状态读取方式存在可复现的兼容性缺陷；真实网银盾最终接受 PIN 仍需修正版部署后的用户正常操作验收。

## 实现边界

本次从整串键盘事件改为按状态和字符处理确认推进。持有输入绑定期间必须固定在同一 OS 线程，不能跨异步续体漂移；不会在字符之间反复绑定、重置状态。超时、取消、失焦或身份变化时停止输入和提交，清理仅释放本次合成输入按下的 Shift，并解除线程绑定。

原有 PIN 本机存储和显示、目标匹配、输入前输入法处理、提交身份复核保持。窗口中的字符数只作为处理进度，不再单独作为整串按键正确处理的依据。

## 验证与待部署包

新回归先在旧整串输入行为上出现 4 项失败，新的专用线程和状态确认实现后通过。执行器最终 85/85，通过报告在 `artifacts/test-results/native-ack/native-executor-ack.trx`。覆盖同线程绑定生命周期、Shift 未确认时等待、取消/失焦/超时不提交及释放自有 Shift、初始 Shift 已按下和无法创建会话时停止。

最终全量 `dotnet test UsbWebLauncher.sln -c Release --no-restore`：1080/1080 通过，报告 `artifacts/test-results/pin-key-state/pin-key-state-final.trx`。独立代码审查未发现确定性问题。

待部署包 `artifacts/staging/pin-key-state-20261008` 仅包含 Windows 组件和重新构建的代理，不覆盖另一任务新增公司面板的 App 文件。Core 依赖与原发布版哈希一致，代理策略也一致。部署后仍需点击“安装环境”刷新受保护代理和主程序绑定。

## 部署记录

用户确认关闭后，2026-10-08 11:36 已替换 Windows 组件及代理的 5 个变更文件，逐个 SHA-256 校验通过。备份：`artifacts/backups/before-pin-key-state-20261008-113602`。本任务未替换 App、Core、配置、策略或用户数据。

- Windows.dll：`6CAFEE0CE2E5A87150A184BB47035E5E023A644E107BFF7088A930A16B407A1C`
- broker EXE：`8A4F8F2C7AC5B02DA2F03BDFF86392D47B3A77D9F5AE96182083014BAC2E8D9D`

公司面板任务另行部署 App.dll/pdb。待双方文件就绪后统一打开主程序，不自动启动业务或提交密码。

公司面板 App.dll 已核对为 `B125316065EA25D33E89F15D924FC23F9FE0E31BFEA7A369F62542EE9F253042`，与新 Windows 组件一起从原发布位置打开。双方更新均已落盘；受保护代理需要用户点击“安装环境”后才使用本次新输入逻辑。

## 11:39 空白输入回退：仍未解决

用户反馈新版不再填入密码。保存现场到 `artifacts/verification/pin-no-input-20261008-1139`：IME 开启到关闭的记录正常，输入前长度为 0，没有 after-input 记录；最终 `FocusLost / InputProcessingWait`，总耗时 4983ms。旧阶段同时表示修饰键等待与字符长度等待，不能据此判断是否已发送首个字符，也不能判断是哪项焦点检查失败。不能把这个状态当作 PIN 被银行拒绝。

没有发现公司面板主动聚焦或激活窗口；它只读 DOM 并更新 TextBlock。既有 `LauncherUiPresenter` 会异步处理 UsbDetected 后激活主窗口，但没有本次事件时序证据证明其参与故障，因此未修改该路径。

新增独立 `artifacts/test-tools/native-executor-probe`，使用完整生产 executor、native API、窗口身份匹配及键盘会话，仅为合成按键增加专属测试标记。只创建自己的窗口，使用假 PIN，不操作银行或真实凭据。此轮运行在唯一一次窗口激活时返回失败，未发送输入。随后只读检查确认当前前台进程为 Windows LockApp；已请用户解锁以继续测试。这仅解释当前隔离测试受阻，不能回推 11:39 的历史失败原因。

本轮代码改动仅补充失败阶段和非敏感故障诊断；尚未替换运行中的发行版，也没有宣称密码问题已修复。后续需要在解锁桌面运行隔离测试，并根据确切证据决定输入逻辑修正，不能再依靠真实银行重复密码尝试排查。

诊断细分 `ModifierProcessingWait` / `CharacterProcessingWait`，保存触发失败的确切分支（前台不匹配、输入焦点不匹配等），以及已发送/已确认字符数量。失败时先释放本次 Shift 并解除绑定，再写一条日志；日志中的附加焦点布尔明确标记为 `after-cleanup`，避免把清理后状态误当成原始触发条件。没有记录 PIN、键值、方向或按键序列，没有放宽输入/提交保护。

新增两项焦点原因测试先失败后通过，最终相关测试 100/100；全量 1090/1090 通过，报告 `artifacts/test-results/pin-no-input-diagnostics/pin-no-input-diagnostics.trx`。诊断包已构建到 `artifacts/staging/pin-no-input-diagnostics-20261008`，仅含 Windows 组件和 broker，没有部署。Core 与已发布版哈希一致，已发布公司面板 App.dll 未改变。测试通过只证明诊断改动回归结果，不代表真实银行密码问题解决。

## 解锁后隔离验证

用户回复“已解锁”后，真实 executor 基线成功：前台激活返回 true，85ms 完成输入和测试按钮确认，8 个合成字符的内部捕获与预期逐字一致，修饰键和布局清理正常。

随后使用银行样式 `0x50010080`（非 ES_PASSWORD），初始 IME 强制开启并双接口确认，真实 executor 在 93ms 完成，合成字符仍逐字一致。该结果排除了这两种测试条件中的确定性回退，不能确认真实银行已解决，也不能仅据 HKL 证明搜狗 TSF 已加载。

第三轮准备验证同步 WM_APP+1 → EM_REPLACESEL 显示路径，在窗口激活阶段失败且未发送输入；只读检查发现前台为全屏游戏。已请用户切回桌面再继续，不重复抢前台。其余独立实验选项已构建但尚未运行：500ms 重装键盘 hook，以及让输入跨越重装时点的 300ms 合成 Shift 回调延迟。所有实验只操作自建窗口和假密码。

## 剩余实验及诊断版部署

用户回复“可以继续”后：消息路径第一轮在确认3个假字符后发生 ForegroundMismatch，127ms 安全停止，清理正常。这轮未捕获触发瞬间前台身份，随后看到其他应用前台不能反推原因。补充 fixture 的前台进程元数据后同模式重跑成功，75ms 完成且8个假字符完全一致。fixture 自身模块检查确认 `sogouModuleLoaded=true`。

500ms 重装 hook 实验加入每次300ms的合成 Shift 回调延迟以跨越重装时点，1305ms成功完成，期间实际重装3次，8个假字符完全一致。所有实验清理均恢复原Caps Lock和布局。没有操作真实银行对话框、提交真实PIN或改变其存储。当前仍无证据确定真实空白输入的根因。

12:22 主程序已关闭时部署诊断增量包，5个文件逐一哈希验证。备份 `artifacts/backups/before-pin-no-input-diagnostics-20261008-122240` 内保存 `deployment.json`。只替换Windows组件及broker，App/Core/策略保持原哈希；这是诊断版，不是已验证修复版。Windows.dll=`44F8F4E71BCB12C337CF9F5D42D76412EBC12E1B3A4272508D66E0B63AB9F06D`，broker EXE=`BE1CB0F35821987D92879563EA8C7CDEED618CB5E6F954EC3FDC3B96C4719560`。已从原发布位置打开主程序；仍需用户点击“安装环境”刷新受保护代理及程序集绑定后，新诊断才用于正常业务。

## 14:10 诊断版现场：首字符没有得到确认

新broker进程14:10:26启动，14:10:36关闭目标IME并读取初始count0。14:10:41返回 `FocusLost / CharacterProcessingWait`，确切原因 `ForegroundMismatch`，`SentCharacterCount=1`、`ConfirmedCharacterCount=0`。证明首个修饰键等待已经完成，SendInput成功插入首字符事件，但尚未得到目标字符数确认；不能把最终失焦视为最初空白输入的原因，也不能把SendInput返回值视为控件接收成功。日志存于 `artifacts/verification/pin-no-character-20261008-1410`。

失败后只读检查pid224576/tid227060的匹配原生对话框：可见、Edit enabled，WM_GETTEXTLENGTH=0，EM_GETLIMITTEXT=8。没有聚焦、输入或提交。vendor WM_KILLFOCUS路径只清hook/timer全局目标，未发现清该Edit显示/内部字符数组；更上层行为未全覆盖。

静态检查进一步排除“scanCode/注入标记过滤”和“事件间隔改变编码”的直接解释：字符callback未读取scanCode/time/extraInfo，未检查注入位；编码只使用焦点时复制的8字节参数，无时间读取或逐字符滚动更新，接收路径先保存编码项并插星号，参数错误本身不应阻止显示首星。这些结论仅限所检查的实际二进制路径。

下一隔离实验改用独立hook线程同步向Edit UI线程发送内部消息，保留真实executor持久Attach/字符数确认；当前WinForms fixture的hook和Edit同线程，不能覆盖这一差异。微软文档也提醒[附着输入队列会引入同步](https://devblogs.microsoft.com/oldnewthing/20130605-00/?p=4163/)以及[同队列SendMessageTimeout不遵守其timeout](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)，但尚无证据证明本次为死锁或哪一调用阻塞。

独立hook实验结果：123ms成功完成，8次hook SendMessage/EDIT接收/显示插入均完成，真实线程owner和焦点检查无误，19次length query，清理卸载成功。因此没有复现一般性的跨线程hook死锁，不能据此直接改SMTO或泵消息。

只读真实凭据元数据检查（`artifacts/test-tools/pin-mapping-metadata`）：全部ASCII字母数字、无控制/空白、非空且<=8、真实发布版builder输出合法、目标HKL下两种Caps状态roundtrip均为true。未输出字符/键码/长度/凭据hash，未输入或提交，secret和数组已清理。

用户手动字符测试最初误答“没有”，随后两次明确更正“有出现星号”“我手动输入有出现星号的”。只读实窗count变为1，Edit仍enabled。以更正为准：控件能够接收手动输入，长度查询能看到结果；不能宣称原生控件本身已卡住。需进一步区分持久Attach改变行为、注入路径或窗口初始化时机，尚无已验证修复。

14:22另一次用户自行重开产生相同首字符零确认；此次CapsLock=true，其他修饰键异步状态false，失败为 `InputFocusMismatch`（前台/active仍匹配），输入前到结束约2.95秒。这支持问题不依赖首字Shift保持。日志保存 `artifacts/verification/pin-no-character-20261008-1422`。随后App和两次目标进程均已退出，不能沿用PID检查或继续实窗实验。

已构建但未运行 `artifacts/test-tools/native-attach-observer`：针对新实窗经严格PID/TID/创建时间/类/control ID/前台焦点确认，只复用生产键盘session连接12秒，独立未附着observer只读字符数。工具不会发送按键/聚焦/提交/读取字符。15秒watchdog仅终止自己启动的child，防止附着期间同步调用阻塞无法清理；同一holder线程调用Dispose。需有新现场才能使用，旧PID不固化。当前轮未改生产输入代码、未更换发行版。

## 实窗连接观察结果

新现场pid15120/tid146672，14:32自动输入仍Sent1/confirmed0。连接观察第一次等待45秒未取得精确焦点，未连接，不作结果。用户配合第二次：5.2秒进入READY并连接生产keyboard session，初始共享Shift/Ctrl/Alt/Caps均false；连接期间长度0→1→0，469次成功采样、0次读取失败，12秒后同OS线程Dispose完成。没有工具注入或提交，输入字母和退格由用户完成。

这一结果排除了“持久连接本身阻塞手动字符处理”，不能排除注入路径或权限差异。此前所有synthetic fixture的sender/UI都为普通权限，真实broker为管理员；下一实验应保持medium自建窗口，仅提升synthetic发送worker，不动受保护broker安装、策略或真实银行窗口。

## 用户要求回滚：已恢复能输入的发行版

用户明确要求先回到“能输入但密码错误”的版本，再重新修复原问题。停止提升权限fixture试验（只构建，未运行）。14:40 正常调用主窗口CloseMainWindow成功退出pid312016，未强制结束进程；从 `artifacts/backups/before-pin-key-state-20261008-113602` 恢复Windows.dll/pdb及broker三文件。全部5文件同时匹配已知能输入的 `artifacts/staging/pin-submit-20261008`，逐一SHA256校验通过。

回滚前备份：`artifacts/backups/before-rollback-pin-batch-20261008-144037`，含 `rollback.json`。回滚后Windows.dll=`15255636010CADC2FBA4048C52C1B2BFCCD3095818C25D0623021BB055FADD77`；broker EXE=`03B1E644AE2D9AF9957194C374A24E6730F40A5C755435D7BD993C5BE66FB35A`。App/Core哈希不变，保留公司面板和流水改动。重新打开主程序pid166416。受保护代理必须通过“安装环境”刷新，已告知用户；回滚不等于原始密码拒绝已解决。

源码仍保留调查期间的逐字符/诊断实现，未把它重新发布；后续构建前必须明确以已回滚的整串输入实现为基线，避免误发已回退方案。原始问题继续从“重设PIN后成功”vs“启动读取已存PIN后失败”的调用顺序与会话状态差异排查，不沿用空白输入回归的假设。

14:41用户自行正常操作后，已恢复的代理日志为 `Success/Completed`：目标pid276912，输入count0→8，完整单次提交，总耗时2114ms。证明回滚已经应用到实际代理，恢复自动填入和确认；不代表银行接受PIN。随后主程序退出，因此未再次擅自打开或发起业务。已询问此次银行最终结果，不要求重复尝试。

重新核对保存vs重启：UI LocalPinCommands.RunSetupAsync实际调用SaveFixedAsync，不调用解锁或Verify；生命周期通知暂存于ManualAuthenticationGate，用户下一次启动才消费。两路StartCycle均进行CSP Acquire(Silent)→SetPin(PP_SIGNATURE_PIN)→GetUserKey，continuation=true都跳过signature probe。重设的区别是先释放旧unlock session/attempt、reset attempted并复用已初始化的WebView；重启新建coordinator和浏览器/原生DLL状态。pending stamp只影响兼容性显示和TLS成功后写stamp，不改变CSP调用；broker协议没有传pending/validated标志。尚无可据此直接修改的根因。

## 15:07 大写原因确认与新版部署

用户确认大写 PIN 在 CapsOff 时错误、CapsOn 时全自动成功。已恢复批量发送源码并加入临时 CapsOn，撤除持续连接的逐字符发送。1119/1119 测试、最终生产组件 CapsOff/On 隔离验证通过。Windows/broker 五文件部署完成，需“安装环境”刷新；真实银行未代为操作。当前实现及验证以 docs/pin-caps-batch-20261008.md 为准，上文排查假设保留为历史。
