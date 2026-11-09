## Context

参见 `proposal.md`。现有银行切换已经具备独立 profile、运行时所有权、会话撤销和延迟回调隔离；`detect-psbc-usb-keys` 负责邮储五个厂商族的只读检测。本变更只补齐邮储企业网银认证，不重新设计 USB 检测。

当前实现中存在四个直接阻塞：

- `psbc-launcher-profile.json` 仍导航到 `https://www.psbc.com/`，且没有邮储认证配置。
- `LauncherProfileValidator` 明确拒绝 `psbcUsbKey` 与客户端证书认证同时出现。
- `WpfWebView2Control` 无条件使用建设银行 WebView2 环境并安装建设银行 UKey 扩展。
- 认证组合只要看到客户端证书配置就会进入建设银行专用页面协调逻辑，邮储不能复用建设银行脚本。

现有 WebView2 证书快照只有 SHA-1、subject 和 issuer。该信息不足以在多证书环境中证明证书属于当前邮储 Key。现有 Native Prompt Broker 已具备调用方校验、窗口复核、聚焦、单次输入、字符数确认和提交能力，但当前 policy 和部分键盘输入约束来自建设银行实机，邮储必须使用独立策略。

## Goals / Non-Goals

**Goals:**

- 在不削弱现有 broker 安全边界的前提下，把邮储业务接入独立的证书和原生 PIN 窗口认证链路。
- 通过证书私钥提供程序的只读元数据，把 WebView2 候选证书与当前邮储厂商/Token 证据关联，而不是依赖截图中的单个证书值。
- 让 WebView2 初始化能力按银行组合，邮储不加载建设银行扩展或页面脚本。
- 在任何不唯一、身份变化、超时、切换或撤销情况下都不输入 PIN。
- 先提供无秘密采集，再根据当前实机证据固化邮储证书和窗口策略。

**Non-Goals:**

- 不复制、打包、反编译或注入“邮储银行网银助手”的专有 DLL、驱动或浏览器组件。
- 不绕过 USB Key 自身的 PIN 重试、锁定、证书有效性或银行 TLS 安全策略。
- 不自动处理验证码、短信、动态口令、登录后的交易确认或业务页面。
- 不承诺在没有实机样本的情况下完成全部五个邮储厂商的硬件验收；公共契约支持多厂商，验收状态按样本分别记录。
- 不把整个 WebView2 或主程序提升到高完整性级别。

## Decisions

### 1. 邮储使用独立认证配置并沿用银行运行时所有权

在邮储 profile 中增加独立的客户端证书认证配置，初始导航地址为 `https://corpebank.psbc.com/#/login`，授权证书端点从 `corpebank.psbc.com:443` 开始。配置使用独立 ID、凭据目标和 native prompt policy，例如：

- 认证 ID：`psbc-client-certificate`
- Credential Manager 目标：`UsbWebLauncher/ClientCertificate/psbc-client-certificate`
- Native Prompt policy：`psbc-uk-password`

这些名称只表达命名空间，不包含证书或 PIN。银行切换继续由现有 session/revision/owner 机制先撤销旧运行时，再构造新运行时；认证回调发布前同时校验 session、revision 和银行 ID。

替代方案是让建设银行和邮储共享一个认证配置，再根据 URL 分支。该方案会共享凭据、窗口策略和浏览器副作用，无法满足银行隔离，因此不采用。

### 2. 把 WebView2 初始化拆成按银行注入的浏览器能力

`WpfWebView2Control` 保留导航、下载、弹窗、证书事件和生命周期管理，但环境选项和扩展安装改由银行浏览器配置提供：

```text
BankFlowDefinition
    |
    +--> CCB browser capabilities
    |       +--> extension-enabled environment
    |       +--> verified CCB UKey extension
    |       +--> CCB page coordinator/scripts
    |
    +--> PSBC browser capabilities
            +--> isolated standard environment
            +--> no CCB extension
            +--> no CCB page coordinator/scripts
            +--> PSBC certificate handler
```

邮储仍使用自己每次业务会话的 user-data 目录，并沿用现有浏览器进程退出后退役目录的清理规则。

替代方案是在当前控件内部通过 URL 判断是否跳过建设银行扩展。URL 不是可信的银行运行时身份，重定向也会让判断脆弱，因此改由已选择的银行定义显式注入能力。

### 3. 证书匹配使用“当前 Key 证据 + 私钥提供程序元数据 + 唯一性”

扩展证书候选快照，在不执行私钥操作的前提下读取证书的 Windows 私钥提供程序绑定信息，至少包括：

- 证书规范化指纹、subject、issuer 和有效期；
- CSP/KSP 名称、provider type、container/key name 和 KeySpec；
- 可安全取得时的证书序列号和扩展用途；
- 数据来源和读取失败分类，不记录私钥或 PIN。

邮储证书策略根据当前 `PsbcUsbKeyDetectionResult` 的厂商族和可用 Token 身份选择允许的 provider/container 规则。候选必须同时满足：

1. 请求端点为明确授权的 HTTPS host/port；
2. 当前银行和业务 session 仍为邮储；
3. 当前 Key 处于 `Ready`；
4. 证书用途和有效期满足客户端认证；
5. 私钥提供程序证据与当前邮储厂商/Token 对应；
6. 完整筛选结果恰好为一个。

当前实机规则必须从 WebView2 实际返回集合中采集后配置。截图中的具体 subject、serial 或任何当前指纹不得写成全局通配规则。若某厂商无法通过只读 provider 元数据建立可靠关联，则该厂商保持“认证不兼容/待采集”，不退化为“选择第一个证书”。

替代方案包括只按 SHA-1 硬编码当前证书，或在 WebView2 只有一个候选时无条件选择。前者不能覆盖多种 Key，后者无法证明证书属于当前 Key，均不采用。

### 4. 邮储原生窗口使用独立 policy，broker 执行逻辑保持通用

现有 Native Prompt Broker 的进程校验、命名管道调用方绑定、随机 attempt ID、防重放、窗口身份复核、焦点建立、输入后字符数确认和确认按钮激活继续复用。新增的是邮储 policy 和无秘密采集，不为邮储创建另一套宽松输入器。

实施前先在当前邮储 Key 上采集：

- 创建窗口的进程名、最终路径、Authenticode 发布者、文件 SHA-256 和完整性级别；
- 根窗口、直接 owner、父子关系、窗口类、标题及可见/启用状态；
- 唯一密码输入控件和唯一“确认”按钮的类、控件 ID、默认按钮属性；
- 窗口与当前 WebView2 主进程/子进程的关系及出现时序；
- 当前键盘布局下 broker 能否使用现有字符输入构建器。

采集模式只枚举公开结构，不读取密码框文本、不接收真实 PIN、不点击确认。证据确认后，将稳定字段写入邮储 policy。运行时只有在证书已经唯一选中、TLS 私钥操作正在等待且窗口完整匹配时才把一次性 attempt 授权发送给 broker。

替代方案是按窗口标题“校验UK密码”发送键盘输入。标题可被其他程序仿冒且无法绑定当前认证尝试，因此不采用。

### 5. PIN 生命周期按银行分区并保持一次尝试

PIN 设置界面根据当前选择银行解析凭据目标。邮储 PIN 通过 WPF 密码控件进入短生命周期缓冲区，写入当前用户 Credential Manager 后立即清零；建设银行和邮储凭据互不覆盖。

邮储认证协调器沿用每个 USB 插入/业务周期最多一次自动尝试的状态机：

```text
Idle
  --> WaitingForReadyKey
  --> AwaitingCertificateRequest
  --> CertificateSelected
  --> AwaitingAuthorizedPrompt
  --> PromptSubmitted
  --> Authenticated | Failed | Revoked
```

任何失败都不在同一周期重试。只有真实拔出并完成旧 session 清理后重新插入，或者用户停止并重新启动形成新业务 session，才可创建新 attempt。只有提供程序明确返回 PIN 拒绝时才删除保存凭据；窗口不匹配、证书冲突、网络失败和超时不能猜测为 PIN 错误。

### 6. 目标 TLS 成功是验证完成的唯一依据

保存 PIN 只产生与邮储认证配置哈希绑定的 pending 标记。证书唯一选择、broker 报告提交成功都不是最终成功；只有授权目标的客户端证书 TLS 导航成功，才升级为正式验证标记。

验证标记至少绑定银行 ID、授权端点、证书匹配策略版本和 native prompt policy 版本。任一配置变化都使旧标记失效，但不自动删除 PIN；下一次新周期重新执行受约束验证。

### 7. 诊断分为普通分类日志和显式无秘密采集

普通日志只记录阶段、银行 ID、厂商族、分类错误和耗时，不记录 Token 身份、证书 subject/serial/fingerprint、窗口文本、控件文本或 PIN 长度。

显式诊断由开发/验收入口启动，输出证书候选及窗口结构的必要公开元数据，并明显标记其中可能包含证书身份信息。诊断输出不得包含 PIN、密码框内容、Credential Manager 内容或私钥操作结果。OpenSpec、测试夹具和提交记录只使用合成证书身份与随机测试 PIN。

## Risks / Trade-offs

- [不同邮储厂商暴露的 provider/container 元数据不一致] -> 以厂商适配规则表达，无法可靠关联的厂商保持失败关闭，并等待对应实机样本。
- [真实证书请求端点可能与入口主机不同] -> 先记录 WebView2 实际请求；只有确认属于官方流程后才把精确 host/port 加入邮储 allowlist，不使用通配域名。
- [邮储密码窗口可能由高完整性厂商进程创建] -> 继续使用已安装的受限 broker；主程序和 WebView2 保持普通权限。
- [窗口版本升级导致标题或控件变化] -> policy 版本化并失败关闭，通过无秘密诊断更新稳定指纹，不做模糊回退。
- [多个 Key 或多个相同厂商证书同时存在] -> 要求当前 Key 证据与证书唯一对应；不能唯一证明时拒绝自动认证。
- [显式诊断包含证书身份元数据] -> 不写普通日志，要求操作者主动启用，并在验收后清理本地诊断文件。

## Migration Plan

1. 增加邮储证书候选和原生窗口的无秘密诊断能力，使用当前邮储 Key 采集实际证据。
2. 泛化 WebView2 银行能力组合，确保建设银行现有扩展和页面协调逻辑的回归测试通过。
3. 扩展 profile 模型和验证器，允许 `psbcUsbKey` 使用邮储独立认证配置，同时拒绝建设银行专属扩展/脚本配置。
4. 增加邮储证书 provider 绑定策略、独立 Credential Manager 目标和认证状态机测试。
5. 根据实机采集结果加入邮储 native prompt policy，并验证零个、唯一、多个窗口候选以及焦点变化分支。
6. 更新邮储 profile 的入口和精确认证端点，构建发布包后完成 Key 已插入及启动后插入两条实机路径。
7. 若发布后邮储认证异常，可关闭邮储认证配置并恢复为仅检测状态；建设银行 profile、凭据和浏览器会话不受影响。
