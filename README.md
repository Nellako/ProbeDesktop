# Probe Desktop

非官方 Windows 桌面客户端。管理 AI API 中转站配置和模型，直连测试，或通过 BazaarLink Probe 手动鉴别和定期巡检。当前源码版本 **0.9.4**。先读 [中文使用手册](使用手册.md) 或 [安全说明](SAFETY.md)。

Windows x64 自包含单文件见 [GitHub Releases](https://github.com/Nellako/ProbeDesktop/releases)。下载 `ProbeDesktop.exe` 后直接运行，不必安装 .NET。程序未签名；请核对来源和 SHA-256，不要关闭安全保护。主要在 Windows 11 x64 验证。

## 开始使用

1. 添加配置，填入 API 根地址（例如 `https://relay.example.com/v1`）、API Key 和协议。支持 OpenAI Responses、Claude Messages、Chat Completions；不会自动补 `/v1`。
2. 确认接收方后拉取模型，或手填模型 ID。单次直连测试只发到对应中转站；连接结果与标记读取分开，不代表模型身份。
3. 两页巡检分别点击“确认巡检设置…”保存范围和间隔。连接巡检默认不重试，可设 0、1、2 次。升级保留开关、范围和历史，但旧授权不再发请求；请重新确认。身份立即巡检每次还需独立确认。
4. 提交身份鉴别会把**完整 API Key、地址、模型和协议**发给 BazaarLink，可能产生费用，报告可能公开。优先使用低额度、可撤销的测试专用 Key。检测只反映本次样本，证据强度不是准确率。

首次空态不联网。最小化到托盘不会停止已开启的巡检。退出不撤回已发出的请求。本地数据默认在 `%LOCALAPPDATA%\ProbeDesktop`，与 EXE 分开；可用 `--data-dir` 指定隔离本地目录。密钥和运行历史由 Windows 当前用户的 DPAPI 保护，换电脑请用密码保护的完整迁移包，不要公开分享迁移包。

## 构建与回归

使用 Windows 和 .NET 10 SDK：

```powershell
.\build.ps1 -Task build
.\build.ps1 -Task test
.\build.ps1 -Task publish -SingleFile
```

发布脚本只生成本地文件，不会上传。程序集和文件版本为 `0.9.4.0`。回归使用假 HTTP 和隔离目录，不代表真实中转站、线上计费或长周期巡检已验证。

[版本说明](RELEASE-NOTES.md) · [Probe API](https://bazaarlink.ai/probe-api-skill.md) · [官方判定方法](https://bazaarlink.ai/zh-CN/probe?tab=learn)
