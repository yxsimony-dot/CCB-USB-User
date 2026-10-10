# Windows 安装包构建

构建目标为 Windows x64，最低产品系统声明为 Windows 10 build 19041。实际已验收的系统必须另外记录，不能由目标框架或编译成功推断。

## 固定输入

- .NET SDK **10.0.100**（`global.json`，运行时仍为自包含 .NET 8）。
- PowerShell **7** 用于构建；安装器运行脚本兼容系统自带 Windows PowerShell **5.1**。
- Node.js **22 或更新版本**；`npm ci` 使用提交的 `package-lock.json`，jsdom 固定为 26.1.0。
- Inno Setup **6.7.3**，调用 `ISCC.exe`。可从 [Inno Setup 官方站点](https://jrsoftware.org/isinfo.php) 获取；也可解包 NuGet `Tools.InnoSetup` 6.7.3，使用 `tools/ISCC.exe`，无需在构建机运行安装程序。工具包来源与校验应记录在构建证据中；不要把下载工具目录提交进仓库。构建会检查实际编译引擎版本，不能依赖 ISCC 的 PE 文件版本（可为 0.0.0.0）。
- 六个 .NET 项目的 `packages.lock.json` 均纳入源码。正常构建只用 locked restore；主动升级依赖时另行更新锁并审查。

## 一键构建

在 Windows Git 干净检出根目录执行：

```powershell
pwsh -NoProfile -File build/build.ps1 -IsccPath 'C:\tools\Inno Setup 6\ISCC.exe'
```

输出目录默认为 `artifacts/releases/1.0.0`，也可用 `-OutputDirectory` 指定一个空目录。禁止复用含旧成品的输出目录，避免把构建失败误认成成功。

构建顺序为工具/版本检查、locked restore、`npm ci`、不含 `Category=Hardware` 的 .NET 测试、浏览器合成测试、安装事务测试、全新目录自包含 publish、载荷检查、Inno 编译、可选签名、校验及清单生成。不会调用真实安装程序，不会注册 broker，不会提交银行业务。缺工具、测试失败、锁不一致或载荷污染均停止。

产物为 `UsbWebLauncher-<version>-win-x64-setup.exe`、`SHA256SUMS`、`release-manifest.json`，不生成 ZIP。构建暂存目录位于 `artifacts/installer-build/<guid>`，不会递归复制现用发布目录。

`build/version.json` 是产品版本、产品标识、安装 AppId、发布通道的唯一来源。App/Broker 的程序集版本和安装器版本均由它派生。只接受稳定三段版本，Windows 文件版本补 `.0`。HEAD 上的产品 tag 必须为匹配的 `v<version>`。正式发布前提交所有源码；本地未提交构建会诚实标记 `sourceDirty: true`，不能当作可由 `sourceCommit` 单独重现的正式发布。

## 签名及后续发布

提供 `-SigningCertificateThumbprint` 后，构建通过 Windows SDK `signtool.exe` 对 App、Broker、最终安装 EXE 进行签名，时间戳地址可通过 `-TimestampUrl` 指定。证书及私钥不存入仓库。未提供证书时仍可构建，清单明确为 `unsigned`；SHA-256 不证明发行者身份，不代表已消除 SmartScreen 提示。

schema v1 清单包含产品/版本/通道/平台/架构、最低系统、源码仓库及提交、安装包文件名/大小/SHA-256、broker 协议和设置 schema。`sourceRepository` 固定为 `https://github.com/yxsimony-dot/CCB-USB-User`。未发布时不生成 `downloadUrl` / `releaseNotesUrl`；未来 GitHub Releases 发布后可填写真实 URL。更新器应校验清单并运行同一安装 EXE，不能另写覆盖程序文件协议。构建不创建 Release、不上传产物、不保存 token。

## 事务测试与人工验收边界

```powershell
pwsh -NoProfile -File tests/packaging/transaction.tests.ps1
powershell.exe -NoProfile -File tests/packaging/transaction.tests.ps1
pwsh -NoProfile -File tests/packaging/build-contract.tests.ps1
```

测试使用临时合成目录验证降级拒绝、同版本修复、健康失败恢复、首次安装恢复已有 broker 的编排、恢复失败保留备份、外层提交前保留备份、未完成事务拒绝、非产品文件保护及快照失败不替换 App。测试中的 broker 回调是明确的合成替身，不会变更真实系统任务/驱动。

仍需在隔离的 Windows 10/11 x64 测试机验证真实 Inno 生命周期、UAC、官方依赖、注册表/卸载程序、升级/中断恢复及 PIN/设置/PDF 保留。自动测试或安装器编译成功不等于这些项目已验收。

## 安装事务维护说明

载荷固定装到当前用户 `LocalAppData/Programs/UsbWebLauncher`；普通卸载仅删除清单登记的产品文件、当前 owner 的 broker 和 Inno 登记，保留用户数据及第三方组件。目录内出现非清单文件时升级会停止，提示用户先移走自己的文件，不擅自删除。

`<app>.transaction` 保存已验证暂存载荷、旧完整 App、技术状态、原 HKCU 卸载登记。broker 通过独立受保护快照保存组件及任务状态。暂存开始即保存 `staging` 状态；最终目录注册和非业务健康验证后，Inno 完成卸载登记，再先记录 `committing`、清理 broker 快照、记录 `committed`，最后清理应用备份。提交中断会检查当前版本健康并继续完成提交，不会尝试使用已删除的 broker 快照回滚。

提交之前的失败恢复旧载荷、原登记和 broker，再检查旧版健康；随后先记录 `restored` 再清理 broker 快照，使恢复过程也可以安全重试。递归清理最后才删除事务状态文件，文件占用导致清理失败不会丢失恢复方向。恢复失败保留备份，用户重跑安装器会获得恢复入口。安装进程被硬中断时不承诺断电原子性。
