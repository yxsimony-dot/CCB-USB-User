<!-- 公开样例：文中的证书身份、指纹和 Key 容器标识已替换为合成数据，不能用于实机认证。 -->
## Why

当前应用已经能够在目标 USB Key 出现后打开内置页面，但每次都会停在客户端证书选择和网银盾密码窗口，尚未实现预期的简化流程。下一步需要在严格限定设备、证书和 HTTPS 主机的前提下，验证并接入 CSP 支持的静默 Key 解锁，再由 WebView2 自动提交目标证书。

## What Changes

- 扩展启动配置，声明允许自动认证的 HTTPS 主机、客户端证书指纹与主题/颁发者，以及当前实测的 CSP 名称、提供程序类型、密钥容器和 KeySpec；配置文件只保存标识，不保存 PIN。
- 在 WebView2 初始化后处理客户端证书请求，仅当主机、端口和唯一证书全部匹配时自动选择证书；缺失、重复或不匹配时关闭自动流程并显示错误。
- 增加本机 PIN 设置入口，由用户在应用本机输入并验证后保存到 Windows 当前用户凭据存储；PIN 不进入源码、JSON、环境变量或日志。
- 增加 Windows CryptoAPI 令牌解锁适配器，使用 `CRYPT_SILENT` 打开 `CCB HD CSP V3.0` 的 `SAMPLE_KEY_CONTAINER_1_RSA` 容器，并按实测的 `AT_KEYEXCHANGE` 使用 `PP_KEYEXCHANGE_PIN`。
- 每次 USB 插入周期最多自动尝试一次；失败后停止并显示可诊断状态，避免错误 PIN 的连续尝试造成令牌锁定。
- 先通过本机兼容性探针验证 CSP 解锁状态能够被 WebView2 的证书私钥操作复用，再启用凭据持久化和默认自动认证。
- 当标准 CryptoAPI/PKCS#11 解锁状态无法被浏览器复用时，允许启用显式配置的厂商 PIN 窗口续接能力：仅在目标 USB 插入周期和目标 TLS 客户端证书请求有效期间，通过独立高权限代理精确校验厂商进程、签名/哈希、窗口类、标题和子窗口结构后提交一次已由令牌验证的 PIN；任何不匹配都默认拒绝。验证码、动态口令、网页业务操作和交易确认仍不在范围内。

## Capabilities

### New Capabilities

- `client-certificate-token-authentication`: 为精确配置的 HTTPS 目标提供本机秘密设置、CSP 静默解锁、WebView2 客户端证书自动选择、单次尝试保护和失败状态反馈。

### Modified Capabilities

无。

## Impact

- 扩展 `UsbWebLauncher.Core` 的认证配置、状态和抽象契约。
- 在 `UsbWebLauncher.Windows` 中新增 Windows 凭据存储、CryptoAPI/PKCS#11 适配器，以及受限的厂商 PIN 窗口匹配与输入适配器。
- 增加与主程序分离的高权限本机认证代理、当前用户受保护 IPC 和一次性管理员安装/卸载流程；内置 WebView2 始终保持普通权限。
- 扩展 `UsbWebLauncher.App` 的本机 PIN 设置界面、WebView2 客户端证书事件处理和依赖组合。
- 增加配置校验、秘密生命周期、证书匹配、单次尝试及本机硬件兼容性测试。
- 依赖 Windows Credential Manager、Advapi32 CryptoAPI、当前已安装的 `CCB HD CSP V3.0` 和可用的 WebView2 Runtime；不引入外部密码服务。
