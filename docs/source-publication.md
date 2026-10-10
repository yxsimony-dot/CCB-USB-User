# 源码发布说明

本工程源码仓库为 https://github.com/yxsimony-dot/CCB-USB-User 。仓库根目录直接包含解决方案、源码、测试和 OpenSpec 变更。

## 公开内容与本地数据

- 提交 `src/`、`tests/`、`docs/` 和 `openspec/`；构建和运行方式见根目录 README。
- `artifacts/`、构建缓存、旧备份、临时交接记录、浏览器会话、下载的 PDF 和本机凭据不提交。
- 历史文档和测试中的实机证书身份与 Key 容器标识替换为合成样例。旧 CryptoAPI 只读预检使用容器摘要保留历史兼容匹配，并接受对应的合成测试样例；当前默认 `BrowserNative` 流程不依赖这些容器名单。
- 需要实际设备的测试不属于普通自动验证。公开测试中的证书与容器样例不能直接用于真实硬件验收，需按本人设备准备本地配置，禁止将实机配置回传仓库。

## 安装包与后续更新

当前上传的是源码，尚未生成通用安装包。安装、依赖准备、版本源、保留设置的覆盖升级和代理重新绑定，见 [安装包变更](../openspec/changes/package-windows-installer-with-upgrade-support/)。

后续安装包拟通过本仓库 GitHub Releases 交付。仓库地址不携带令牌，运行程序不依赖 Git 登录；将来的签名材料与发布凭据应在本机或 CI 密钥存储中配置。
