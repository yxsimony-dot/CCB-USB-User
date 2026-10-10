<!-- 公开样例：文中的证书身份、指纹和 Key 容器标识已替换为合成数据，不能用于实机认证。 -->
# 发布验证记录

## 2026-09-30 Release 目录收敛

在正式版主流程真机验收成功后，按用户要求将 `RELEASE` 目录收敛为单一发布分支。
当前仅保留：

`F:\USB-Web-Launcher\RELEASE\win-x64-pin-unicode-input`

已删除以下旧回滚副本：

- `win-x64-pin-unicode-input.rollback-20260929-before-foreground-activation`
- `win-x64-pin-unicode-input.rollback-20260929-before-input-confirmation`

本次清理未修改当前正式版目录，也未删除其中的应用配置或运行数据。

## 2026-09-30 正式版主流程真机验收成功

用户按正式发布路径手动更新认证组件并重新启动应用后，确认整个目标认证流程
完全正确。此次验收使用已插入的当前 Tendyron Key，覆盖了正式版启动、目标证书
选择、令牌解锁、原生密码弹窗识别、固定 PIN 自动输入、单次确认以及后续目标
TLS 流程。

不含秘密的 Broker 诊断记录：

- 本地时间：2026-09-30 09:13:21。
- `Status=Success`。
- `Stage=Completed`。
- 枚举窗口数：62；候选数：1；完整匹配候选数：1。
- 从枚举到完成耗时：1477 毫秒。
- 诊断没有记录 PIN、PIN 字符数、按键内容或输入文本。

计划任务 `UsbWebLauncher Native Prompt Broker` 已指向
`C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe`
并使用 `--server` 参数以最高可用权限运行。当前发布应用能够与该 Broker 完成
严格策略匹配和一次成功提交，因此确认当前自包含 `win-x64` 发布及当前镜像绑定
已经生效。

本次证据完成 OpenSpec 任务 `3.1`、`3.3` 和 `4.2`。运行中插入 UKey、故意构造
不匹配弹窗以及拔出后重新插入的新周期仍属于独立验收场景，未由本次主流程成功
自动推定为完成。

## 2026-09-29 取消阶段诊断发布

在一次由用户显式启动的真机验收中，原生弹窗策略成功枚举并唯一匹配到目标
`msedgewebview2.exe` 窗口，但客户端证书流程约 30 秒后取消。原有诊断会把取消时
的实际阶段统一覆盖为 `Cancelled`，因此无法区分输入处理等待和提交按钮等待。

本次修正只增强无秘密诊断：

- 取消结果仍为 `Cancelled`，但诊断中的 `Stage` 保留取消发生时的安全执行阶段。
- 诊断不新增 PIN、字符数、按键、输入文本或 Credential Manager 内容。
- 精确窗口、控件、前台、焦点、初始空输入、完整输入计数和单次提交要求保持不变。
- 随机测试秘密覆盖了 `PolicyValidation`、`ForegroundWait`、`InputBuild`、
  `InputProcessingWait` 和 `SubmitWait` 的取消路径。

验证结果：

- 原生弹窗执行器定向测试：62/62 通过。
- Release 全量测试：773/773 通过，0 失败，0 跳过。
- `openspec validate complete-tendyron-pin-autofill --strict --json` 通过。
- 自包含发布输出：
  `C:\tmp\UsbWebLauncher-publish-20260929-stage-diagnostics`。
- 发布文件数：492；总大小：290,046,416 字节。
- 发布包内 6 个 JSON 文件全部成功解析，且没有 `.log`、`.trx`、PCAP、ETL
  或 `.WebView2` 运行残留。
- 新发布文件已复制到
  `F:\USB-Web-Launcher\RELEASE\win-x64-pin-unicode-input`，关键文件与暂存发布
  哈希一致。
- `UsbWebLauncher.App.dll` SHA-256：
  `D24EEFBED9D482A246B6F42716582861DFF65645CC1DE459B794EED309999677`。
- `UsbWebLauncher.Windows.dll` SHA-256：
  `E2978EF784C8999AF808FA4A08D0C849EE63477C46FD47F1CD07941C9D41600E`。
- `broker\UsbWebLauncher.NativePromptBroker.exe` SHA-256：
  `6F815964C251AB692049110F4035C0BBC47D93AE94C5547929970EF9072827D6`。

管理员 Broker 更新的 UAC 请求由用户取消，因此当前安装在
`C:\Program Files\UsbWebLauncher\NativePromptBroker` 的 Broker 尚未绑定这次新发布。
下一次手动真机验收前，需先在应用中点击 `安装/更新认证组件` 并允许 UAC；完成后
再关闭应用，按验收步骤重新启动。此次未自动重试 UKey，也未勾选任何真机验收任务。

日期：2026-09-26

## 2026-09-29 最终发布部署、代理绑定与秘密复核

当前待验收发布目录：

`F:\USB-Web-Launcher\RELEASE\win-x64-pin-unicode-input`

切换发布前已保留完整回滚目录：

`F:\USB-Web-Launcher\RELEASE\win-x64-pin-unicode-input.rollback-20260929-before-input-confirmation`

- 回滚目录包含 986 个文件；其中旧版 `UsbWebLauncher.App.exe` SHA-256 为 `EE3F7E5CD3B185774D32ED302BA07FE277773FD9CCFAB26E18B2E0BDE4670A88`。
- 当前发布 `UsbWebLauncher.App.exe` SHA-256：`1838EFCF696E1F7832936DE9E640AA2C85F982F5CFFCF0E398A18A92082220A3`。
- 当前发布 `UsbWebLauncher.App.dll` SHA-256：`D27ED0C4DFB7E7C25D9C5422F8C6483D779F2D2766EAAD22626E9D4855654224`。
- 当前发布 `UsbWebLauncher.Core.dll` SHA-256：`58B05416EDCD030626C0456B0779A462F6298B885D98506883CD7CE343D45877`。
- 当前发布 `UsbWebLauncher.Windows.dll` SHA-256：`36582BE783819B6E81C67B05F304C5A6D42EF5D7F8CCC09147E11CCB1DD97433`。
- 当前发布 `launcher-profile.json` SHA-256：`3930819BD5DA6F1A1A49B6CE803D3F6A97881B53F6D540A822839CEDF0963646`。
- 当前发布 `broker\UsbWebLauncher.NativePromptBroker.exe` SHA-256：`03D275DB842676DAAB81485B5C04F0418F14C68B9C8AA613EAE25A17400463B9`。
- 当前发布 `broker\native-prompt-policy.json` SHA-256：`5ECEBA730DC11FD999ABAB53DE4B30B83BCA66EDDC506013C10CE1D212EA33E4`。

本机原生提示代理部署核验：

- 计划任务名称：`UsbWebLauncher Native Prompt Broker`；状态为 Running，参数为 `--server`，以最高权限运行。
- 已安装代理路径：`C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe`。
- 已安装代理文件哈希与当前发布代理一致。
- 已安装公开策略协议版本为 `1`，策略 ID 为 `ccb-hdzb-client-certificate-v1`，命名管道为 `UsbWebLauncher.NativePromptBroker.v1`。
- 已安装策略内部哈希为 `9311D46AFDACAD17976F69ABF8E26A4867DCA0812697600161ECE9753FC8045C`。
- 策略精确绑定 `msedgewebview2.exe` 的 `#32770` 窗口 `验证通用盾密码`、`Edit(3743)` 输入控件和 `Button(4000)` 提交控件。
- 已安装 TrustedClient 对当前应用程序、应用程序集、核心程序集和 Windows 实现程序集的四项哈希绑定均与当前发布目录一致。
- 使用当前发布执行 `--smoke-test-no-devices` 后，主窗口显示 `USB Web Launcher [Smoke Ready]` 和 `本机认证组件已就绪。`，预期与当前协议版本均为 `1`；随后正常关闭。该检查不探测 UKey、不读取凭据、不访问银行站点。

最终秘密与输入通道复核：

- 扫描生产文本文件 82 个、测试文本文件 45 个、配置/文档文件 17 个、发布文件 492 个。
- 发布目录 6 个 JSON 文件全部成功解析；禁止的 PIN、密码或秘密字段为 0。
- `Clipboard`、`SendKeys`、`keybd_event`、`WM_SETTEXT` 等不安全输入 API 命中 0。
- 生产代码中的秘密环境变量入口、命令行秘密传输和直接秘密赋值均为 0。
- 项目与发布目录中的 `.log`、`.trx`、`.WebView2`、ETL 或 PCAP 残留为 0；已知测试秘密标记在发布目录中命中 0。
- 数字模式命中仅来自公开 USB VID/PID、XAML 宽度及测试夹具。测试中的 `"pin"` 字段只用于验证客户端证书认证配置会拒绝秘密字段，不进入发布包。
- 本记录、扫描输出和发布文件均未读取、显示或记录真实 PIN。

代理卸载命令：

```powershell
Start-Process `
  -FilePath 'F:\USB-Web-Launcher\RELEASE\win-x64-pin-unicode-input\broker\UsbWebLauncher.NativePromptBroker.exe' `
  -ArgumentList '--uninstall' `
  -Verb RunAs `
  -Wait
```

固定 PIN 的首选清理方法是在应用程序中点击 `删除本机 PIN`；该操作同时删除验证印章和当前用户 Credential Manager 条目。若应用无法使用，可打开 Windows“凭据管理器”→“Windows 凭据”→“普通凭据”，删除目标名称以 `UsbWebLauncher/ClientCertificate/` 开头的条目。清理时不查看、不导出凭据内容。

## 2026-09-26 建行 UKey 扩展与当前 Tendyron Key 发布

- WebView2 已启用浏览器扩展，并在首次导航前校验、安装或启用随包发布的中国建设银行 UKey 扩展。
- 扩展 ID：`ahjacjkbgeefngpfnfhccehkfnaeahbf`；名称：`中国建设银行Ukey扩展`；版本：`1.0.0.9`。
 - 入口页保持为 `https://b2bbank.ccb.com/...`，客户端证书只允许精确匹配 TLS 端点 `b2b3.ccb.com:443`。
- 当前 Tendyron 证书、CSP、容器和 `AT_SIGNATURE` 密钥规格均由发布配置精确绑定；本记录不包含固定 PIN。
- 原生提示续接策略哈希：`804064DD54206E06D9CF1E39C1C56A49FBCF4EF2550C077FFD80EAC9B1FDA5AB`。

Release 全量回归结果：702/702 通过，0 失败，0 跳过。执行环境使用 .NET SDK `8.0.425`；隔离终端显式补齐 `APPDATA`、`LOCALAPPDATA`、`PROGRAMDATA`、`DOTNET_CLI_HOME` 和 NuGet 缓存路径后执行：

```powershell
& 'C:\tmp\DotNetSdk8\dotnet.exe' test .\UsbWebLauncher.sln -c Release --no-restore --verbosity minimal
```

发布目录：`C:\tmp\UsbWebLauncher-publish-20260926`。

- 文件数：491；总大小：289,984,687 字节（276.55 MiB）。
- `UsbWebLauncher.App.exe` SHA-256：`1838EFCF696E1F7832936DE9E640AA2C85F982F5CFFCF0E398A18A92082220A3`。
- `UsbWebLauncher.App.dll` SHA-256：`AE5FE4D34B35A19CE57E38E363CA35E8A589D062E091EFD960FB1DC02B739328`。
- `launcher-profile.json` SHA-256：`2E902EED3F31D718985059B1C2341711F8D1C58C8BFE645CF11C352BDA6DD0D0`。
- `broker/UsbWebLauncher.NativePromptBroker.exe` SHA-256：`7C66C0714F04E9FCC95B5F03B20453AAA8C1E026A9DFE201A5C0872B3F10B704`。
- `broker/native-prompt-policy.json` SHA-256：`DC61154BFA36640053F3862A6655BF3B84BC9BF15E98A3719F8C371357598067`。
- 扩展源目录和发布目录各 6 个文件，逐文件 SHA-256 全部一致。
- `--smoke-test-no-devices` 退出码为 0。
- 发布包中的 5 个 JSON 文件全部成功解析，PIN、密码或秘密字段命中 0。
- 真实源码中的 `Clipboard`、`SendKeys`、`keybd_event`、`WM_SETTEXT` 命中 0。
- 发布包中的 `.log`、`.trx` 和 `.WebView2` 运行数据残留为 0。

本机未找到先前记录的 OpenSpec CLI 路径，因此本轮未重复执行 strict validate；OpenSpec 文档和任务清单已手工同步。真实 USB Key、扩展、证书选择、固定 PIN 续接和目标 TLS 成功仍需在切换桌面发布目录后完成最终验收。

## 自动测试

使用 D 盘 .NET SDK 和 NuGet 缓存执行：

```powershell
$env:DOTNET_CLI_HOME='D:\DotNetCliHome'
$env:NUGET_PACKAGES='D:\NuGetPackages'
& 'D:\DotNetSdk8\dotnet.exe' test UsbWebLauncher.sln -c Release --no-restore
```

结果：682/682 通过。默认测试没有访问真实 USB Key、真实站点或真实 PIN。

## 秘密与输入模拟审计

扫描范围包括 `src`、测试配置、最终发布目录、日志/测试结果残留和已知测试秘密标记：

- JSON 中名称包含 `pin`、`password` 或 `secret` 的字段：0。
- 未受约束的 `SendKeys`、`keybd_event`、`WM_SETTEXT`、剪贴板等输入模拟入口：0。`SendInput` 只存在于独立原生续接代理的单次批量输入路径，并由受信客户端 PID、窗口/进程签名、唯一控件和输入前焦点复核共同约束。
- 生产源码中的环境变量秘密入口：0。
- 生产源码和最终发布目录中的已知测试 PIN 标记：0。
- 最终发布目录中的 `.log`、`.trx` 或 `.WebView2` 浏览器运行数据：0。
- 测试中的秘密值均为带 `test`/`fake`/`smoke` 语义的固定标记或运行时随机值；未发现 4–12 位纯数字字符串形式的候选真实 PIN。

证书指纹、主题、颁发者、CSP 名称、容器名和凭据目标名称属于非秘密配置标识。审计无法证明未知格式的外部秘密从未被人工复制进任意二进制，因此发布前仍保留人工复核要求。

## 自包含发布与无 PIN 冒烟

发布命令：

```powershell
& 'D:\DotNetSdk8\dotnet.exe' publish src\UsbWebLauncher.App\UsbWebLauncher.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -o artifacts\publish\win-x64-native-prompt-fixed-credential-navigation-fix

& 'D:\DotNetSdk8\dotnet.exe' publish src\UsbWebLauncher.NativePromptBroker\UsbWebLauncher.NativePromptBroker.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -o artifacts\publish\win-x64-native-prompt-fixed-credential-navigation-fix\broker
```

结果：

- 输出目录：`artifacts/publish/win-x64-native-prompt-fixed-credential-navigation-fix`。
- 文件数：485。
- 总大小：289,780,064 字节。
- `UsbWebLauncher.App.exe` SHA-256：`3DD0205ABF99A582997046B5C81E9C178094CF369171198E7F7E79D3A1B8B8AC`。
- `UsbWebLauncher.App.dll` SHA-256：`50AC9E98AE09C51A45A3C166C3501A48A22EE169FE9545C87919EB5D5027111A`。
- `broker/UsbWebLauncher.NativePromptBroker.exe` SHA-256：`EFB96A7A28A381828D413D2E38B2D255E81CB89D98C01FCCEBE0C3FFE0ED81B7`。
- `broker/native-prompt-policy.json` SHA-256：`5665C9A7538DD39DEEE0AAAEA535034EA6A969000141BC8C75C2C6AF04741D34`。
- 发布配置与 `src/UsbWebLauncher.App/launcher-profile.json` 的 SHA-256 一致。
- 使用显式 `--smoke-test-no-devices` 模式加载当前完整认证配置；待启动稳定后连续两次正常关闭，退出码均为 0，且 Windows 应用程序日志中没有新增对应的 .NET Runtime 崩溃事件。
- 该模式由生产启动入口强制替换为空设备监视器，不依赖伪造 VID/PID，因而不会读取凭据、初始化目标站点导航、访问网络或接触真实 USB Key。临时运行副本和 WebView2 用户数据已删除。

## 本次故障修复验证

- 主窗口关闭清理已延后到原始 `Closing` 事件返回之后，避免窗口关闭期间再次调用 `Close()` 导致 `0xE0434352` 未处理异常。
- CryptoAPI 解锁结果会保留不含秘密的失败阶段，并连同原生错误码传递到认证状态；测试覆盖了“设置 PIN / `0x80090020`”的显示和 PIN 不回显。
- 发布包扫描结果：未受约束输入入口 0、环境变量秘密入口 0、配置秘密字段 0、`.log`/`.trx`/`.WebView2` 用户数据残留 0。
- 实机确认 WebView2 密码框是由受信 WPF 主窗口直接拥有的顶层模态 `#32770`，不是 `EnumChildWindows` 返回的普通子窗口。修复后只允许直接 owner 等于本次受信主程序精确根窗口的候选；此路径允许 owner 因模态关系处于禁用状态，并激活密码框本身后聚焦 `Edit(1010)`。错误 owner、根窗口自引用或其他应用实例仍默认拒绝。
- 新增 owned-dialog 匹配、激活、“代理先武装、弹窗稍后出现”以及“计划任务已停止、旧代理映像仍未释放”竞态回归测试后，全量 Release 测试 668/668 通过；针对当前真实弹窗的无输入预检 1/1 通过；新发布包无设备冒烟连续两次通过。
- 实机确认安全输入框接受虚拟回车但过滤 `KEYEVENTF_UNICODE` 字符包。代理现把每个字符映射为当前键盘布局下的虚拟键，按需要生成成对 Shift 事件，并拒绝 Ctrl/Alt 映射、不可映射字符、部分输入和任何自动重试。
- 固定 PIN 的一次录入与端到端验证已分离：只保存在当前用户 Credential Manager，并写入与配置哈希绑定的非秘密待验证标记；USB Key 出现后自动读取一次，目标 TLS 成功才升级为正式验证印章。普通的印章缺失、旧哈希或未知标记不会授权读取凭据。
- 修复目标 USB 已插入时保存固定 PIN 后只把空白浏览器标记为 Ready、却没有执行 WebView2 导航的问题。设置命令现在等待认证周期准备完成，并通过启动协调器的真实设备处理路径重新检查认证门、初始化浏览器和导航；回归测试同时验证该异步恢复操作会被等待。
- 新增上述流程、错误 PIN 跨重启保护和真实导航恢复测试后，全量 Release 测试 682/682 通过；OpenSpec 严格校验通过；新发布包无设备冒烟连续两次通过。

## OpenSpec 位置

- CLI：`D:\CodexTools\OpenSpec\openspec.cmd`。
- apply skill：`D:\CodexSkills\openspec-apply-change\SKILL.md`。
- `openspec status --change add-client-certificate-auto-selection --json` 可正常读取当前项目。

## 实机无秘密预检

设置 `USB_WEB_LAUNCHER_HARDWARE_TEST=1` 后显式运行 `Category=Hardware` 测试，结果 2/2 通过：SetupAPI 能识别配置的 USB 型号；CryptoAPI 能以 `CRYPT_SILENT` 打开 `CCB HD CSP V3.0`、RSA Full、`SAMPLE_KEY_CONTAINER_1_RSA` 并取得 `AT_KEYEXCHANGE` 密钥句柄。该预检没有读取或提交 PIN、没有执行私钥签名，也没有访问目标网站。

## 尚未执行

针对当前仍打开的实机密码框，设置 `USB_WEB_LAUNCHER_HARDWARE_TESTS=1` 和非秘密的 `USB_WEB_LAUNCHER_TRUSTED_CLIENT_PID` 后运行原生提示框预检，结果 1/1 通过：只找到一个绑定到该主程序实例、Microsoft 签名 WebView2 进程、`#32770` 对话框、`Edit(1010)` 密码框和 `Button(1)` 确认按钮的候选。该预检没有聚焦控件、提交 PIN 或调用 `SendInput`。最终发布包的真实自动提交与 TLS 验收仍需通过应用本机密码框由用户显式执行；本记录不包含真实 PIN。

## PP_KEYEXCHANGE_PIN 参数修正

实机诊断在“设置 PIN”阶段返回 `0x8009000A (NTE_BAD_TYPE)`。复核 Windows `wincrypt.h` 约定后确认，`PP_KEYEXCHANGE_PIN` 的值应为 `32 (0x20)`，原实现及其测试误用了 `12 (0x0C)`。这表示程序传入了错误的参数类型，不表示用户密码错误。

- 先把假原生层测试的独立期望改为 `0x20`，确认旧实现以“期望 32、实际 12”失败；修正生产常量后该测试通过。
- 全量自动测试：346/346 通过。
- 本机无 PIN 硬件预检：2/2 通过。
- 修正版目录：`artifacts/publish/win-x64-pin-param-fix`；使用独立目录是因为实机验收时旧版进程仍锁定原发布目录，未强制终止用户进程。
- 文件数：480；总大小：195,536,242 字节。
- `UsbWebLauncher.App.exe` SHA-256：`751CE1C0AAE95C036D46F873D9D536C52A756D7331C2D8BD6D6F52872E94777E`。
- `UsbWebLauncher.App.dll` SHA-256：`F06CE0A253FC2B6B45DBB09666F6527689F77C4D210FB736401A7ED28C41C1BE`。
- `UsbWebLauncher.Windows.dll` SHA-256：`266FF49044D6E6EBD9756F92B42B8DEC65EF0A8F6395308D7B51361216E6C6E0`。
- 无设备启动及正常关闭冒烟通过，退出码为 0。
