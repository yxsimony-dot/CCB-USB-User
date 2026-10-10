# 建行证书登录页自动填写验证记录

日期：2026-09-30。

## 行为与入口

用户明确同意扩展“启动业务”后的流程：沿用 USB Key 检测、证书选择和原生 PIN 认证，到达受支持的建行证书登录页后，读取“设置 PIN”保存的同一凭据，填写网页“登录密码”，点击原有“登录”按钮一次。

打开 EXE、安装环境和设置 PIN 不触发该流程。网页提交失败、超时、控件不匹配或密码框已有内容时不自动重复提交；重复页面回调不增加提交次数。拔盾或业务取消会取消仍在等待的自动填写。状态只确认已提交或未完成，不把点击登录视为银行已认证成功。PIN、含 PIN 的脚本及可能包含秘密的异常内容不写入日志，不新增凭据文件。

继续使用唯一快捷方式 `F:\USB-Web-Launcher\启动 USB Web Launcher.lnk`，实际发布目录仍为 `F:\USB-Web-Launcher\PROJECT\artifacts\publish\win-x64-manual-actions-20260930`，没有新增启动入口。更新应用后需通过“安装环境”刷新现有代理的文件哈希绑定。

## 公开页面核实

只读获取了配置入口的公开登录页及其已引用脚本，没有提交登录请求或读取真实 PIN。

- 公开页面地址为 `https://b2bbank.ccb.com/NCCB/MNECV6B2BMainPlat_UN`，入口查询参数包含 `TXCODE=N7N010` 和 `LOGIN_TYPE=UC`。
- 公开页的证书登录区域为 `#Wrap_searchbar`，表单为 `form#form_id[name=jhform]`，账号字段为 `#CUSTOMERID` 和 `#USERID`。
- 密码字段为普通 `input#LOG_PWD[name=LOG_PWD][type=password]`，登录按钮为 `input#LOGIN_BOX_BTN.btn_js_link1[type=button]`。网页自己的 `input/change` 处理程序在账号和密码非空时启用按钮；按钮处理程序执行原有加密与请求流程，因此使用按钮点击，不直接提交表单。
- 公开脚本的 `toCTLoginJsp()` 会跳转到 `https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp?PT_LANGUAGE=CN`。无客户端证书的只读请求返回需要证书的页面，无法据此核实 TLS 认证后的最终表单。截图中的“登录密码:”及下拉框不能单独证明字段 ID 与公开页相同。

## 验证结果

发布状态：用户确认关闭程序后，已将 6 个 DLL/PDB 更新到原发布目录，并逐一核对发布文件与暂存清单的 SHA-256 一致。原快捷方式仍指向同一个 EXE，未创建其他启动入口。发布哈希见 `artifacts/verification/certificate-page-login-20260930/deployed-sha256.json`，部署结果见同目录 `result.json`。再次打开后，先点击“安装环境”刷新代理对更新程序集的哈希绑定，再点击“启动业务”；无需重新设置已保存的 PIN。

- 全量 Release 测试：835/835 通过，0 失败、0 跳过。命令：`dotnet test PROJECT/UsbWebLauncher.sln -c Release --artifacts-path F:/USB-Web-Launcher-Build/page-login --verbosity quiet`。
- 页面脚本行为测试：10/10 通过，使用 Node + jsdom 26.1.0 和合成数据，覆盖事件启用、单次点击、错误来源、重复字段、用户已输入、控件替换、账号变化、禁用按钮和可见证书表单识别。
- 在 Chromium 浏览器中运行同一脚本的本地模拟页面，确认填写触发页面原有事件、点击一次、第二次提交被阻止。没有访问真实银行提交接口。
- 新增协调器及集成用例先失败后通过；独立审查发现的启动取消残留授权、页面检查无法取消等待两项问题已修复。拔盾、准备操作和旧回调不会授权下一次提交。
- 只有原有 CS0067（测试替身中未使用的 NavigationStarting 事件）编译警告。
- Windows x64 自包含发布构建成功，临时构建位于工作区外。发布只需替换主程序及引用项目的 6 个 DLL/PDB 文件；EXE、配置、浏览器运行时、扩展和 broker 文件均与现有包哈希相同。
- 外置构建第一次测试有 4 项因旧测试从输出目录向上寻找仓库夹具而失败；补入原始配置和扩展夹具后全量通过。不是业务测试失败。

脚本首先识别公开页的精确字段；证书后的页面若使用不同 ID，只接受唯一、可见的“登录密码”密码框，且必须位于含“证书登录”、两个已填账号字段和唯一“登录”按钮的同一表单中。提交前再次验证来源、表单、账号和文档标记，保留页面本身的加密和校验流程。脚本与含凭据参数不写入日志。

## 尚待实机验收

本次开发验证没有执行真实银行登录。真实 U 盾完成 TLS 后的最终 URL、表单匹配、一次填写和登录结果仍待用户实际使用验收；公开源核实与本地测试不能替代这一步。遇到未匹配控件时应保留页面供手动处理，不扩大匹配到任意密码框，不反复尝试。

## 用户反馈后的排查

用户实测未填写也未点击登录，超过一分钟仍显示“页面已就绪”。已用真实 WPF Button 的 STA 回归测试复现：后台页面登录通知先进入视图模型的按钮状态订阅者，引发 `ButtonBase.UpdateCanExecute` 跨线程异常，阻断后续状态提示，且可能影响关闭清理。修正为通过界面 Dispatcher 发布通知，单个订阅者异常不阻断其他订阅者。

修正后全量 .NET 测试 837/837、页面脚本测试 11/11 通过。开始识别页面时会显示“正在识别证书登录页…”。新增本机诊断文件 `%LOCALAPPDATA%/UsbWebLauncher/logs/certificate-page-login.jsonl`，只记录流程枚举、来源和路径、控件属性及可见性；不记录 PIN、账号输入值、完整查询字符串、脚本载荷或异常正文。合成秘密值不出现在诊断结果的测试已通过。

状态通知修正不能证明真实证书表单已匹配。下一次实机运行需利用诊断继续核实控件与页面地址。用户确认关闭程序后，已更新原目录的 App DLL/PDB，两项 SHA-256 均与暂存清单一致；原快捷方式目标不变。状态见 `debug-update-result.json`，发布哈希见 `debug-deployed-sha256.json`。重开后先点击“安装环境”刷新程序集绑定，再点击“启动业务”，以本次实际页面结果与诊断为准。

## 内嵌页面根因与修订

下一次实机日志确认：顶层 `https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp` 已加载完成，但没有任何输入框或表单；其可见同源 frame 指向 `/NCCB/V6/b2bmainV7.jsp`。此前脚本只查询顶层文档，因此一直返回 waiting。脱敏结构证据已保存在 `artifacts/verification/certificate-page-login-20260930/frame-discovery.json`。

修订仅扩展到已授权顶层页面下可见、同源、位于 `/NCCB/` 的内嵌文档，最多 4 层、16 个文档。识别唯一候选文档后保留原来的完整登录表单检查；密码设置器和事件使用内嵌文档自己的浏览器对象。提交前重新核对文档标记和 frame 可见性；跨域、隐藏、多个候选或替换后的文档不会收到 PIN。

本次 7 项 frame 回归测试中，修复前 6 项按预期失败；修复后全部脚本测试 18/18，通过全量 .NET 测试 837/837。Chromium 中的实际同源 frame 模拟页也确认填写、页面事件和一次点击成功。仍未执行真实银行登录。用户确认关闭程序后，已更新原发布目录的 App DLL/PDB，逐一核对 SHA-256 与暂存清单一致；唯一快捷方式未变。部署状态见 `frame-update-result.json`，文件哈希见 `frame-deployed-sha256.json`。重开后先通过“安装环境”刷新应用程序集绑定，再启动业务进行实机确认。

## 重复表单 ID 误判与修订

用户随后看到“当前登录控件不支持自动填写”。实机诊断确认 `https://xc.b2b3.ccb.com/NCCB/V6/b2bindex.jsp` 的内嵌 `/NCCB/NECV6B2BMainPlat_09` 文档中，证书登录表单 `jhform` 和辅助表单 `jhform2` 共用 `form_id`。密码框 `LOG_PWD`、账号字段及 `LOGIN_BOX_BTN` 均已找到并属于 `jhform`，但全页面 ID 唯一性检查拒绝了实际登录表单。结构证据见 `duplicate-form-discovery.json`，没有记录字段值。

修订先唯一识别 `form#form_id[name="jhform"]`，再在其内部寻找并检查控件；其他表单的重复 ID 不再干扰。同名证书表单、登录表单内部重复控件、错误控件归属仍拒绝。按钮不再依赖外观 CSS 类名，继续核对原始按钮身份、类型、文字、实际表单归属以及启用状态；保留原有 input/change 和点击处理。独立审查发现按钮 form 属性可指向另一表单的情况，已补充检查及回归测试。

新增 8 项页面回归测试，相关失败用例在修复前已复现，最终脚本测试 26/26、全量 .NET 测试 837/837 通过，Windows x64 发布构建成功。最终脚本在 Chromium 的同源 frame 加重复表单 ID 模拟页中完成一次填写和点击，并阻止重复提交。所有测试使用合成数据，尚未证明真实银行登录成功。

用户确认关闭应用后，已更新原目录 App DLL/PDB 并核对暂存与发布 SHA-256。实际内容变化仅 App DLL；PDB 哈希保持相同。唯一快捷方式和唯一主程序 EXE 已核对。记录见 `duplicate-form-update-result.json`、`duplicate-form-staged-sha256.json` 和 `duplicate-form-deployed-sha256.json`。重开后先点“安装环境”刷新代理哈希绑定，再点“启动业务”；无需重新设置 PIN。
