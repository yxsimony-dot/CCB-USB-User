# USB Web Launcher

面向建设银行企业网银的 Windows 桌面工具：检测 USB Key，在内嵌 WebView2 中启动网银、填写已保存的 PIN，并按所选月份导出流水和电子回单。

本仓库保存源码、测试、安装包构建脚本及需求设计。Windows x64 安装包提供固定目录安装、环境准备和保留用户数据的覆盖升级；使用方法见 [安装与升级](docs/installation.md)，构建方法见 [构建说明](build/README.md)。main 推送通过完整测试与构建后自动发布到 GitHub Releases，客户端可检查并下载安装新版，见 [检查更新与自动发布](docs/updates.md)。

## 当前功能

- **Key 状态提示**：打开应用和插拔设备时检测，未启动业务也能显示是否存在可用 Key；检测本身不启动登录。
- **启动 / 中止业务**：手动启动一轮业务；运行中可中止并关闭本轮页面。拔出 Key 会清理本轮业务，重新插入后需要手动启动，不恢复旧页面。
- **本机 PIN**：设置、更换、删除和显示当前 PIN，按原始大小写输入。PIN 保存在当前 Windows 用户的凭据管理器中，同一用户下共用一份，不按每个 Key 分别绑定。
- **认证与登录**：当前默认采用 `BrowserNative`，由银行驱动及浏览器完成认证，通过独立本机代理填写兼容的验证密码窗口，并在受支持的证书登录页面填写登录密码。登录后显示当前公司，自动关闭已适配的公告和推广弹窗。
- **导出流水和回单**：选择 01～12 月，一次点击后顺序导出当年该月的流水、电子回单。共用同一网页，不并行操作；每项查询前按流程回到首页。
- **下载目录**：当前 Windows 用户独立保存目录设置，默认桌面；同名文件追加序号，不覆盖已有文件。
- **检查更新**：启动时后台检查一次，也可手动检查；用户确认下载、校验完整安装包并在业务空闲时升级，保留本机数据。

流水失败或取消时停止。确认流水为空时停留当前页面，提示“流水页面为空，不需要导出该月份流水。”，不继续导出回单。回单只下载查询结果**第一页**的记录；若回单失败，保留已经保存的流水。未来月份不提交查询，当前月按完整月份范围查询。

文件命名规则：

```text
MM-公司全名-建行账号后4位-对账.pdf
MM-公司全名-建行账号后4位-回单.pdf
```

其中 `MM` 为两位月份，例如 `09`；账号末四位保留前导零。

## Key 识别与兼容范围

配置位于 `src/UsbWebLauncher.App/launcher-profile.json`。当前 `ccbUsbStor` 策略包含两条识别路径：

1. USB 硬件标识匹配当前收录的 20 组建行 Key VID/PID。
2. 或者使用 `USBSTOR` 驱动服务，且**同一设备关联**的虚拟光盘名称或卷标包含 `CCB`，忽略大小写。

设备目录见 `src/UsbWebLauncher.Core/Devices/CcbDetectorDeviceCatalog.cs`。识别到设备不代表驱动、证书、PIN 或银行页面已就绪，也不代表这 20 组设备均经过真实端到端验证。

默认 `BrowserNative` 不预先按固定证书指纹、厂商 CSP 或容器解锁指定 Key；仍保留建行目标地址与本机窗口所属进程等检查。可用证书不唯一时不会任意选择。不同驱动的密码控件及银行页面仍可能需要适配；验证码、动态口令、硬件按键或交易确认不在自动填写范围内。

## 运行条件与首次使用

- Windows 10（19041 或更高版本）/ Windows 11，x64。
- Microsoft Edge WebView2 Evergreen Runtime。
- 建行官方 E 路护航及对应 Key 驱动、浏览器认证组件；本项目不能替代银行官方组件。
- 自包含发布目录携带 .NET 运行时，使用者无需另装 .NET SDK。

普通用户使用安装包并按 [安装与升级](docs/installation.md) 操作。开发者从自包含发布目录运行时：

1. 安装 WebView2 和银行官方组件，确认银行工具能识别自己的 Key。
2. 以普通用户身份运行 `UsbWebLauncher.App.exe`，保留完整发布目录及 `broker` 子目录。
3. 点击“安装环境”，按 UAC 提示安装本机认证代理。**该按钮当前仅安装本项目代理，不安装 WebView2 或银行驱动。**
4. 点击“设置 PIN”，保存自己的 PIN，并按需选择下载目录。
5. 插入 Key，点击“启动业务”；登录后选择月份，点击“导出流水和回单”。

本机认证代理安装在 `%ProgramFiles%\UsbWebLauncher\NativePromptBroker`，计划任务绑定安装时的交互用户。当前不承诺多个 Windows 用户可同时独立使用该代理。主程序及 WebView2 使用普通用户权限，代理安装需要管理员确认。

代理校验调用程序的最终路径和文件哈希。完整安装器会刷新代理绑定；开发者手动更新或移动发布目录后，应在新版本重新点击“安装环境”刷新绑定。PIN 不随发布目录转移；其他电脑或其他 Windows 用户需要自行设置。目录设置及浏览器临时数据位于当前用户的 `%LOCALAPPDATA%\UsbWebLauncher` 下。

需要移除本机代理时，使用“安装环境”旁菜单中的卸载入口。删除 PIN 使用“设置 PIN”旁的删除入口；卸载代理不会代替删除用户凭据。

## 开发与测试

开发环境需要 Windows x64、global.json 固定的 .NET SDK 10.0.100（应用仍以 .NET 8 为目标）；页面脚本测试另需 Node.js 22 或更新版本及 npm。在仓库根目录运行：

```powershell
dotnet test UsbWebLauncher.sln -c Release --filter 'Category!=Hardware'

npm ci --ignore-scripts
npm test
```

自动测试覆盖状态机、设备匹配、认证代理及合成页面交互。它们不能替代真实 Key、银行页面和全新电脑的安装验证。

## 从源码发布

将应用和本机代理发布到一个**全新的、尚未运行过的目录**。以下输出路径为示例；重复发布时请换用新的空目录，避免混入旧缓存或文件。

```powershell
dotnet publish src/UsbWebLauncher.App/UsbWebLauncher.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -o artifacts/publish/win-x64-clean

dotnet publish src/UsbWebLauncher.NativePromptBroker/UsbWebLauncher.NativePromptBroker.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -o artifacts/publish/win-x64-clean/broker
```

发布后保留 `launcher-profile.json`、浏览器扩展、所有运行库以及 `broker` 内未绑定的策略文件。不要复制本机已安装代理的绑定策略、个人 PIN、日志、浏览器缓存或下载的 PDF 作为发行内容。

`UsbWebLauncher.App.exe --smoke-test-no-devices` 可用于无真实设备的启动冒烟检查：该模式不枚举真实 Key、不读取 PIN、不导航网银，不能证明真实银行业务可用。

## 工程结构与后续版本

- `src/UsbWebLauncher.App`：WPF 界面、WebView2、页面脚本及导出流程。
- `src/UsbWebLauncher.Core`：设备匹配、业务状态与认证抽象。
- `src/UsbWebLauncher.Windows`：Windows 设备、凭据及本机认证实现。
- `src/UsbWebLauncher.NativePromptBroker`：本机密码窗口输入代理。
- `tests`：.NET 单元测试和 Node 页面脚本测试。
- `openspec`：功能规格和变更任务。
- `docs`：历史设计与验证记录，其中旧认证策略、个人开发路径和当时的部署状态不代表当前版本。
- `artifacts`：本地构建、测试和诊断产物，不作为源码提交。

[安装包与升级支持变更](openspec/changes/package-windows-installer-with-upgrade-support/) 记录统一版本号、稳定安装标识、保留用户设置的覆盖升级和升级后的代理重新绑定。[客户端更新变更](openspec/changes/add-github-client-updates/) 接入 GitHub Releases、应用内更新和 main 自动发布；已验证项目及尚未实测的目标机器场景见 [更新验证记录](docs/update-validation.md)。
