## Why

邮储银行 Key 检测已经接入独立银行分支，但“启动业务”仍打开错误页面，也没有邮储专用的客户端证书选择和 UK PIN 自动填写链路。现在需要把邮储流程补齐为可独立运行、默认拒绝不确定目标的企业网银认证能力，同时保持建行流程不受影响。

## What Changes

- 将邮储“启动业务”的入口配置为 `https://corpebank.psbc.com/#/login`，仅在用户当前选择邮储银行时启动该流程。
- 为 `corpebank.psbc.com:443` 增加邮储专用的 WebView2 客户端证书请求处理，只在当前邮储 Key 对应的证书能够被唯一确认时自动选择。
- 增加无秘密诊断，采集 WebView2 实际证书候选的公开元数据以及邮储“校验UK密码”窗口的进程、签名、窗口和控件结构，禁止把截图中的单个证书编号或窗口猜测硬编码为通用规则。
- 为邮储配置独立认证 ID、Windows Credential Manager 目标、原生窗口策略和 WebView2 用户数据目录；邮储流程不加载建行 UKey 扩展，也不执行建行页面脚本。
- 在目标证书请求有效且邮储原生密码窗口通过完整身份复核时，使用已保存的邮储 PIN 填写唯一密码输入框并确认；窗口不唯一、身份变化、超时或控件不匹配时不输入 PIN。
- 每次邮储业务认证周期最多自动提交一次 PIN；零个或多个证书匹配、原生窗口匹配冲突、Key 拔出、银行切换或会话撤销均以失败关闭结束。
- PIN 只通过本机密码输入控件和当前 Windows 用户的 Credential Manager 流转，不进入 JSON、日志、命令行、剪贴板、网页脚本、诊断输出或 OpenSpec 文件。
- 增加离线测试和当前邮储 Key 的实机验收，覆盖 Key 已插入和启动后插入、证书唯一/缺失/冲突、窗口匹配/拒绝、银行切换及失败关闭。

## Capabilities

### New Capabilities

- `psbc-business-authentication`: 邮储企业网银入口导航、当前 Key 的客户端证书唯一匹配、邮储 UK PIN 原生窗口安全续接、凭据隔离和认证生命周期要求。

### Modified Capabilities

无。

## Impact

- `UsbWebLauncher.Core` 的银行专用认证配置、证书匹配策略、认证状态和诊断契约。
- `UsbWebLauncher.Windows` 的 Credential Manager 目标、证书元数据采集及邮储原生窗口匹配策略。
- `UsbWebLauncher.App` 的邮储 profile、WebView2 环境创建、客户端证书事件处理、PIN 设置入口和银行运行时组合。
- 邮储认证相关单元测试、集成测试、无秘密诊断工具和实机验收记录。
- 依赖本机已安装且可用的邮储 Key 驱动、加密提供程序、WebView2 Runtime 和现有 Native Prompt Broker；不复制或分发邮储网银助手的专有组件。
