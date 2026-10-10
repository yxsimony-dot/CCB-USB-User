# 实机网页登录按钮歧义修复（2026-10-09）

上一版在认证跳转稳定后仍返回 unsupported。通过应用临时、仅本机的调试端口直接采集无值 DOM 元数据，确认真实 `/NCCB/NECV6PubQuery` 页的同一登录表单有一个 INPUT `LOGIN_BOX_BTN` 和两个文字均为“登录”的 A 链接。密码框为空、表单归属一致，POST action 为允许的 `/NCCB/NECV6B2BMainPlat`。拒绝来自把三个同名元素都作为提交按钮，而非 PIN 或 Key 不匹配。

修复为按控件角色分级：原生 input/button 优先，没有时再考虑 role=button，最后兼容 A 链接。多个同级真正按钮仍拒绝。未新增 Key、表单 ID 或页面型号特例。

新增真实嵌套路径加两个同名链接的回归先失败；修复后在仍打开的真实页面执行只读 inspect，得到 ready。该探针不提供 PIN、不点击登录、不采集输入值，仅检查 passwordEmpty 布尔和控件结构。

最终 .NET 1356/1356、浏览器 246/246 通过。正式 App DLL SHA-256 为 `B35AF0E28A002E9BC80099D18A5EB2319E82C4F65F14AE971A81FC4D63891625`。发布前备份：`artifacts/backups/before-login-button-priority-20261009-162913`。重新安装受保护组件以更新 App 哈希绑定，退出码 0。

实机验证中第一次因 FocusLost / FocusValidation 在输入前终止；将应用置于前台后使用程序自身正常启动流程再验证，16:30:59 原生认证 Success / Completed，16:31:00 网页识别 ready、凭据 Available=true、提交结果 Submitted。没有通过探针读取或直接输入 PIN，没有执行导出或交易。

最终程序 PID 62216，Responding=true；当前公司占位提示已消失。这里只确认此次 Key 的自动填写与点击登录链路，未声称全部实体 Key 均已实测。临时调试端口 19379 已关闭，最终程序按普通方式启动并保持打开。

证据目录：`artifacts/verification/login-live-diagnosis-20261009/`，其中 `live-metadata.json` 为值脱敏的真实结构，`live-inspect.json` 为修复后识别结果，`final-login-metadata.jsonl` / `final-native-status.txt` / `final-runtime.json` 为正式版本运行结果。
