# 重启后 PIN 显示正确但网银盾报错

## 结论与边界

用户反馈 EXE 重启后出现网银盾密码错误，重新保存同一 PIN 后恢复。现有日志显示停在原生证书认证；网页密码填写流程尚未启动，因此没有证据支持两路自动填写同时提交。

尚未在隔离环境复现真实网银盾的密码拒绝，不能宣布本次故障已完全解决。没有读取产品凭据用于实验，没有提交真实 PIN，没有消耗网银盾认证次数。

## 已验证

- 新增 pending / validated 印章两种重建认证协调器测试，包含显示副本读取和释放、重新保存相同合成 PIN、比较解锁器和原生代理收到的完整字符。两个路径一致。
- 独立临时 Windows 凭据：父进程、子进程、子进程退出后的父进程各重建 100 次，每轮读取两个副本并释放显示副本，共 600 次逐字符检查通过。临时凭据最后删除并验证不存在。
- 自建原生 EDIT 控件、线程池及独立子进程模拟代理输入：大小写锁定开关实验均成功，未证实 Caps Lock 根因。
- 中文输入法开启时，隔离 EDIT 会进入组合态而非直接写入字符；关闭时正常。这个现象会导致现有长度检查超时，不能直接等同于用户看到的密码错误。

## 限定修正

原生映射原先使用 `VkKeyScanW`，对应代理线程自己的布局。现在获取已匹配密码输入控件的窗口线程布局，再通过 `VkKeyScanExW` 转换；没有独立输入控件时使用已匹配窗口。缺少窗口线程或布局时停止输入，不回退到代理布局。临时恢复代理布局后，布局差异回归测试失败；恢复目标布局后通过。

没有更改 Caps Lock 算法或主动切换输入法，没有替换成安全控件已知会过滤的 Unicode 包，没有增加自动认证次数。

## 新诊断

`%LOCALAPPDATA%\UsbWebLauncher\NativePromptBroker\input-environment.jsonl`

记录目标和代理布局、代理读到的 Caps Lock 状态、Shift/Ctrl/Alt/Win 是否按下、IME 开关（无法读取时为 null）、输入前后已有长度检查的字符数、时间和进程标识。不读取密码框文本，不记录 PIN、按键序列或凭据摘要。文件在 64 KiB 后轮转，最多保留当前和上一份；重启不会清除历史。诊断失败不触发重试。

下次用户正常登录后的这份日志可用于比较重启和重新保存 PIN 的环境差异。`last-execution-status.txt` 中的 Success 只代表自动输入与按钮激活流程结束，不代表银行接受 PIN。

## 验证与发布

回归测试覆盖完整 PIN 生命周期、目标布局选择、目标句柄传递、缺失布局拒绝输入、诊断顺序、日志轮转与不可写路径。隔离实验脚本保留于 `artifacts/test-tools/credential-restart-probe` 与 `artifacts/test-tools/native-pin-probe`。

本次改动涉及 Windows 程序集和原生代理。发布后必须在主界面点击一次“安装环境”，以更新受保护代理和绑定；仅替换主程序不足以启用代理中的修改。

最终 `dotnet test UsbWebLauncher.sln -c Release --no-restore`：1034/1034 通过。主程序与单文件原生代理均发布成功，替换原快捷方式对应的 `artifacts/publish/win-x64-manual-actions-20260930` 下 7 个变更文件，并逐个校验 SHA-256。用户配置、代理策略及 WebView2 数据未替换。备份目录：`artifacts/backups/before-pin-input-20261008-101444`。

审查额外发现同步诊断可能延迟最后一次焦点检查，因此在 `SendInput` 紧前增加前台、输入焦点、取消、超时复核；对应 4 个测试在修正前均失败，修正后通过。

参考：微软 [VkKeyScanExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-vkkeyscanexw)、[GetKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getkeystate)、[AttachThreadInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-attachthreadinput)。
