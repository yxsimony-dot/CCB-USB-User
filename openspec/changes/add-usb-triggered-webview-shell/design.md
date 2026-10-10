<!-- 公开样例：文中的证书身份、指纹和 Key 容器标识已替换为合成数据，不能用于实机认证。 -->
## Context

设备实测表明，当前 Key 通过 USB 大容量存储接口暴露，硬件 ID 为 `USB\\VID_1677&PID_0054`，总线描述为 `uKeyCI800-K156`，并挂载 `HDZB-CCB USB AutoRunDisk`、卷标 `HDZB_USBKEY_KEY` 的 CDFS 虚拟光驱。该设备与网页选择的 `GSB00000000003` 客户端证书通过 `CCB HD CSP V3.0` 和 `HDZB_CSP.dll` 建立了只读关联。PnP 实例后缀、容器 ID 和端口路径随接入位置或枚举周期变化，不能作为持久身份。行为约束见 `specs/usb-triggered-webview-shell/spec.md`。

## Goals / Non-Goals

**Goals:**

- 建立可独立测试 USB 匹配与应用状态转换的最小架构。
- 将 WPF、Windows 设备 API 和 WebView2 细节限制在适配器及组合根中。
- 让后续页面事件、脚本执行和秘密来源可以作为新能力加入，而无需重写 USB 触发主链路。
- 生成可运行的 Windows x64 发布目录和 EXE。

**Non-Goals:**

- 首版不设计通用流程 DSL、流程录制器、凭据模型或原生窗口自动化。
- 首版不实现实体 Key 的唯一身份认证，也不把 USB 检测作为安全边界。
- 首版不封装 Chrome、CEF 或 Electron 浏览器后端。
- 首版不保证目标网站的所有插件、安全控件或客户端证书流程都可在 WebView2 内完成。

## Decisions

### Use .NET 8 WPF with a WebView2 adapter

应用采用 `net8.0-windows` 的 WPF 外壳，WebView2 由独立浏览器适配器承载。适配器负责初始化、导航、重试以及把初始化和导航结果转换为与 UI 无关的状态事件；核心协调器不直接引用 WebView2 控件。

选择 WebView2 是因为它提供 Chromium/Edge 渲染能力、官方 WPF 控件和后续脚本/消息扩展点，同时无需随应用打包完整浏览器。CEF 和 Electron 会显著增大第一版体积与维护面；直接控制 Chrome 又不能提供稳定的内嵌窗口和独立宿主生命周期。

### Separate the solution into core, Windows infrastructure, and desktop composition

解决方案使用以下项目边界：

- `UsbWebLauncher.Core`: 设备描述、匹配规则、应用状态、协调器和接口；不依赖 WPF、PnP 或 WebView2。
- `UsbWebLauncher.Windows`: Windows 设备枚举和监听实现；将系统属性映射为核心设备描述。
- `UsbWebLauncher.App`: WPF 界面、WebView2 适配器、配置加载和依赖组合。
- `UsbWebLauncher.Tests`: 匹配规则、状态转换、去重及错误/重试测试。

相比把所有逻辑放在 WPF 项目中，这一边界略增项目数量，但可以用伪设备源和伪浏览器验证主链路，也为以后替换监听或浏览器后端保留空间。首版不引入完整插件框架或通用依赖注入抽象。

### Use Windows device enumeration and notifications, then evaluate pure matching rules

Windows 适配器使用设备枚举 API 获取启动时快照，并监听设备添加、移除和属性更新。每次相关通知触发一次去抖后的重新枚举，解决父 USB 设备和虚拟光驱子设备分阶段出现的问题。系统属性先映射为不可变设备描述，再由核心中的纯匹配器判断。

默认规则要求：

- Hardware ID 包含 `VID_1677` 和 `PID_0054`；
- 总线描述等于 `uKeyCI800-K156`（忽略大小写）；
- 虚拟光驱名称、卷标与文件系统仅作为可配置辅助条件。

不使用固定盘符、端口位置、容器 ID 或完整 PnP 实例 ID。盘符可能改变，端口和容器信息也不是设备型号的稳定身份。相比只轮询 `E:`，设备通知能覆盖启动前已插入、启动后插入和不同盘符场景。

### Drive the UI through an explicit single-flight state machine

协调器采用以下状态：`WaitingForUsb`、`UsbDetected`、`OpeningBrowser`、`Ready`、`Error`。设备事件和浏览器事件经单一协调器串行处理，并在触发打开时使用单次执行门闩，防止重复 PnP 通知创建多个窗口或重复导航。

应用启动时显示一个轻量状态窗口，浏览器区域保持折叠；匹配后在同一窗口中显示 WebView2、将窗口带到前台并导航。Key 在页面打开后被移除时，仅更新设备状态，不销毁浏览器会话。错误状态提供“重试”命令，重试当前失败步骤而无需重启进程。

### Keep site and device values in a replaceable profile

首版使用一个 JSON 配置文件，包含 `targetUrl`、USB 匹配条件和可选辅助条件。仓库默认配置指向 `https://b2b.ccb.com/` 并使用本次实测的设备特征，但应用名称、核心类型和逻辑不出现行业语义。

配置在启动时一次性读取并校验：目标必须为绝对 HTTPS URI，VID/PID 必须为四位十六进制文本。配置错误进入可诊断错误状态，不回退到任意网站或任意 USB 设备。首版不实现运行时热重载。

### Reserve browser automation and secret access as future adapters only

核心仅依赖浏览器初始化、导航和状态契约。WebView2 实例保留在适配器内部，因此以后可在该层新增页面脚本、DOM 事件或消息通道。秘密来源若以后需要，将作为独立能力和独立接口设计；首版既不创建空的密码 UI，也不读取环境变量或本机密钥。

这避免现在冻结尚未确认的密码命名、作用域和生命周期，同时不妨碍后续扩展。

### Publish as a Windows x64 application directory

首版产物采用 `win-x64` 发布目录，包含应用 EXE 和所需托管/本机组件，并依赖目标机器上的 WebView2 Evergreen Runtime。暂不追求单文件封装；WebView2 本机加载器和诊断文件在普通发布目录中更容易验证和排障。

## Risks / Trade-offs

- [同型号设备都能通过匹配，不能证明是某一只实体 Key] → 在 UI 和文档中明确这是启动条件而非认证；未来如驱动或令牌 API 暴露唯一标识，再新增实例级匹配能力。
- [设备属性分阶段出现导致短暂漏检] → 对相关设备通知去抖后重新枚举完整快照，并测试通知顺序变化。
- [WebView2 Evergreen Runtime 缺失或损坏] → 捕获初始化失败并显示修复提示；发布检查中加入运行时存在性测试。
- [目标网站未来依赖 WebView2 不支持的本机安全控件] → 保持浏览器适配器边界，待出现实际需求后再评估浏览器外的原生 UI 适配器，不在首版预建。
- [WPF UI 线程与设备回调线程竞争] → 协调器串行化事件，UI 更新统一调度到 WPF Dispatcher。
- [自包含 x64 发布目录体积高于框架依赖发布] → 优先保证目标机器可直接运行；如部署环境统一具备 .NET 8，可另增框架依赖发布配置。

## Migration Plan

这是绿色项目，无已有用户数据或旧版本需要迁移。实施完成后先运行自动化测试，再在当前机器上分别验证“启动前已插入”和“启动后插入”两种路径，最后生成 `win-x64` 发布目录。回滚只需退出并移除该发布目录；首版不安装驱动、不注册系统服务，也不修改 USB Key。
