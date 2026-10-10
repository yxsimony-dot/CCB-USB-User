## Why

当前已验证的功能集中在开发机上的发布目录，其他 Windows 电脑还缺少统一的依赖检查、本机认证组件注册和首次配置入口，不能直接交付整个现用目录。需要一个干净、可从源码重建的安装包，并固定产品身份和升级契约，避免以后通过 GitHub 分发新版本时丢失 PIN、下载目录或破坏认证组件绑定。

## What Changes

- 仅交付 Windows x64 安装程序，安装主程序、配置、建行浏览器扩展和自有认证组件包；不提供 ZIP 成品，不把本机缓存、日志、PIN、证书或业务文件带入安装包。
- 采用当前 Windows 用户的固定应用目录、稳定安装 AppId 和统一版本源，提供开始菜单入口、卸载入口及版本显示；主程序正常启动时不提升权限。
- 首次使用检查 WebView2 Runtime、建行官方驱动及浏览器原生通信组件，允许联网和必要的管理员确认。通过官方安装流程满足缺失依赖，未就绪时明确显示待完成项。
- 在最终应用路径本地注册 broker，首次设置本机 PIN；升级保留现有 PIN、下载目录及已下载文件，重新绑定新版应用路径和程序集哈希，并提供安装失败后的整包恢复。
- 明确一期为一台电脑的一个指定交互 Windows 用户：不覆盖其他用户的 broker 所有权，不将提升所用的另一管理员身份当作业务用户；不承诺多用户并行、SYSTEM 静默部署或跨账号提升可用。
- 从干净源码执行可重复的构建与安装包生成，生成版本、校验和及发布清单，预留 `https://github.com/yxsimony-dot/CCB-USB-User` 的 GitHub Releases 分发契约。本期不实现自动更新、自动发布或后台联网版本检查。

## Capabilities

### New Capabilities

- `windows-installer-distribution`: Windows x64 安装包、干净载荷、官方依赖初始化、本机用户范围和卸载边界。
- `installed-application-upgrades`: 稳定安装身份、覆盖升级、凭据与配置保留、broker 重新绑定及失败恢复。
- `release-build-contract`: 统一版本源、干净源码构建、安装产物校验和、未来 GitHub Releases 清单契约。

### Modified Capabilities

无。现有业务按钮、PIN 使用、Key 识别和导出流程不在本次改变；新增安装编排复用现有应用接口。

## Impact

新增打包与构建脚本、安装器定义、版本/发布清单和安装验收。涉及 App 版本展示、首次运行依赖状态以及现有 broker 安装/健康检查的调用与用户所有权检查。保留 Windows Credential Manager 和 `%LOCALAPPDATA%\UsbWebLauncher` 的用户数据位置；不能将已有 broker 自身回滚误当作整包升级回滚。需要干净 Windows x64、覆盖升级和权限边界实机或虚拟机验收；第三方运行库/银行安装器采用经核实的官方来源，不复制开发机已安装环境。
