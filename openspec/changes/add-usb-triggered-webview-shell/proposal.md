## Why

当前项目需要一个最小、可扩展的 Windows 桌面骨架：在指定型号的 USB Key 插入后自动打开目标网页，并为后续网页流程辅助保留清晰的扩展边界。第一版只验证 USB 触发与内置浏览器承载这条主链路，避免在实际需求尚未确定时提前构建密码、验证码或流程自动化功能。

## What Changes

- 新增 Windows 桌面应用骨架，启动后等待配置指定的 USB Key。
- 首版按当前设备的型号级特征识别：USB `VID_1677`、`PID_0054`，总线描述 `uKeyCI800-K156`；可使用 `HDZB-CCB USB AutoRunDisk`、卷标 `HDZB_USBKEY_KEY` 和 `CDFS` 文件系统作为辅助校验。
- 匹配成功后显示应用窗口，并在内置 WebView2 中打开配置的目标地址。
- 对 USB 检测、浏览器承载、应用协调和配置读取进行接口隔离，为以后增加页面脚本、事件和密钥来源保留扩展点。
- 第一版不提供密码输入或保存、验证码/动态口令转填、网页流程 DSL、原生安全控件操作或 Chrome 控制。

## Capabilities

### New Capabilities

- `usb-triggered-webview-shell`: 等待配置匹配的 USB 设备，在检测成功后显示 WebView2 宿主并导航到配置网址，同时暴露清晰的状态和扩展边界。

### Modified Capabilities

无。

## Impact

- 新建基于 .NET 8、WPF 和 Microsoft Edge WebView2 的 Windows 桌面项目及测试项目。
- 运行环境需要 Windows、可用的 WebView2 Evergreen Runtime，以及读取本机即插即用设备信息的普通用户权限。
- 第一版只识别设备型号，不保证区分同型号的不同实体 Key；当前设备未向系统暴露稳定的硬件序列号。
