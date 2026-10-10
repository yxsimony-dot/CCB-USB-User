# 源码发布说明

本工程源码仓库为 https://github.com/yxsimony-dot/CCB-USB-User 。仓库根目录直接包含解决方案、源码、测试和 OpenSpec 变更。

## 公开内容与本地数据

- 提交 `src/`、`tests/`、`docs/` 和 `openspec/`；构建和运行方式见根目录 README。
- `artifacts/`、构建缓存、旧备份、临时交接记录、浏览器会话、下载的 PDF 和本机凭据不提交。
- 历史文档和测试中的实机证书身份与 Key 容器标识替换为合成样例。旧 CryptoAPI 只读预检使用容器摘要保留历史兼容匹配，并接受对应的合成测试样例；当前默认 `BrowserNative` 流程不依赖这些容器名单。
- 需要实际设备的测试不属于普通自动验证。公开测试中的证书与容器样例不能直接用于真实硬件验收，需按本人设备准备本地配置，禁止将实机配置回传仓库。

## 安装包与后续更新

首次源码上传没有包含安装文件。现有安装包构建入口、依赖准备、版本源、覆盖升级和代理重新绑定说明，见 [安装与升级](installation.md) 与 [安装包变更](../openspec/changes/package-windows-installer-with-upgrade-support/)。安装 EXE 及附属清单在本地 artifacts/releases 生成，不作为源码提交。

main 推送通过完整测试与构建后，安装包通过本仓库 GitHub Releases 自动交付，客户端可检查更新。仓库地址不携带令牌，运行程序不依赖 Git 登录；签名材料与发布凭据只在本机或 CI 密钥存储中配置。版本、失败重试和首次升级方法见 [检查更新与自动发布](updates.md)。
