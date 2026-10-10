# 更新功能验证记录

## 本地集成验证（2026-10-10）

源码提交：`3b83e56fc1e95a438b21e115bc32dc75cb0f514e`。

- 完整非硬件 .NET 测试：1,701 通过，0 失败。
- 浏览器合成测试：292 通过，0 失败。
- 发布替身测试：14 通过；版本解析、构建失败阻断、载荷检查、安装事务及完整性脚本通过。
- 实际 WPF 渲染检查了维护菜单、更新提示和更新窗口；使用合成服务，没有进入银行业务或启动安装器。
- 独立审查未发现阻断问题；发布端与客户端最大安装包限制已统一为 1 GiB。
- 从干净提交运行完整构建入口，生成本地 `1.1.0` 安装包，81,156,190 字节，SHA-256：`93da15bc08cb1dee1a45a680a505635917f9ea15223f8a915925c3d77c252349`。
- 核对 App EXE、App/Core/Windows DLL、Broker EXE、安装 EXE 均为 `1.1.0.0`；载荷 product.json、发布清单版本及源提交一致，构建未修改源码。

本地 `1.1.0` 是验证产物；面向用户交付 GitHub Actions 生成的更高发行版本。

## 首次云端运行发现的测试环境差异

[运行 38044835671](https://github.com/yxsimony-dot/CCB-USB-User/actions/runs/38044835671) 的源码为 `e8bdbdcc4875ceee5987bd863d1293c65f25adff`。非硬件测试 1,693 通过、8 失败，发布 job 被跳过，未公开 Release。

- 7 个原生窗口测试在实际布局捕获成功后，错误地假定测试进程一定为中等完整性；GitHub Windows runner 以管理员身份运行。测试现核对真实身份，并分别断言中等完整性布局匹配和高完整性拒绝，生产规则不变。
- 1 个目录链接测试创建 fixture 的 PowerShell 子进程超时，未退出的子进程又与清理发生竞争；日志不足以判断超时位于启动还是命令内部。fixture 改为同步设置 Windows mount-point reparse data，继续验证真实链接可读、产品拒绝清理且外部文件保留。

这些修复不修改认证、Key 检测或目录清理的运行时代码，也未跳过失败用例。后续 main push 采用新的发布序号，失败运行留下版本空号是预期行为。

## 真实发布与匿名客户端验证

[运行 38045270561](https://github.com/yxsimony-dot/CCB-USB-User/actions/runs/38045270561) 的 build 和 publish 均成功，源码为 `df1b3ea3982d9e91900295a9aecbbf408e252bf5`。云端通过 1,701 项非硬件 .NET、292 项浏览器、14 项发布测试及安装包装脚本，校验 486 个载荷文件后生成完整安装包。

[正式版本 1.1.2](https://github.com/yxsimony-dot/CCB-USB-User/releases/tag/v1.1.2) 于 2026-10-10 10:37:23 UTC 公开。三个资产均为 uploaded，发布前已由 CI 重新下载核对。安装包 81,144,345 字节，SHA-256：`b008670e1badcdcab2a02b2ab75bb149ad86d4d9c65447c85cb8fd6fcc0b1f63`。

本机还完成以下验证，均未启动安装器：

- 不带账号或 token，下载正式 EXE、清单和 SHA256SUMS；复核大小、散列、源提交和安装器文件版本 `1.1.2.0` 一致。
- 使用实际 `GitHubApplicationUpdateService` 和真实 HTTP 传输，从 `1.0.0` 得到 `UpdateAvailable:1.1.2`，完成下载和 `VerifyDownloadedAsync` 复核；从 `1.1.2` 得到 `UpToDate`。
- 上述成功客户端验证使用测试 harness 显式直连网络。本机系统代理出口的 GitHub 匿名额度已耗尽，默认代理路径真实返回 `RateLimited`；普通直连请求成功。没有将 token 加入客户端，没有修改用户系统代理设置，生产客户端仍使用系统网络配置。

当前正式下载入口为 [GitHub Releases](https://github.com/yxsimony-dot/CCB-USB-User/releases)。本记录提交本身也会按用户选择触发下一次版本发布；后续版本通过相同测试和发布门槛，不会覆盖 1.1.2 的正式资产。

## 尚未进行的实机验收

没有可用的隔离 Windows 10/11 安装环境。本次没有运行真实安装升级、UAC 取消、安装中断恢复、PIN/目录/PDF 保留或真实 Key 认证验收。自动化替身测试、实际窗口渲染及安装包编译不能替代这些项目；任务 5.3 保留未完成。
