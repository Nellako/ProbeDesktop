# Probe Desktop

非官方 Windows 桌面客户端，用于管理 AI API 中转站配置、拉取模型列表、直连连接测试，以及调用 BazaarLink 公开 Probe API 进行手动鉴别和身份识别巡检。当前版本 **0.9.1**。

支持 OpenAI Responses、Claude Messages、Chat Completions 三种显式协议。无账户体系、无云同步，不自建云后端。第三方判断不是模型真实身份或能力的确定性证明；本软件不提供“降智总分”。

## 下载与使用

从 [GitHub Releases](https://github.com/Nellako/ProbeDesktop/releases) 下载 Windows x64 发布包，解压后运行 `ProbeDesktop.exe`。自包含单文件包携带 .NET 10 运行时，无需另外安装运行时。程序未签名，可能出现“未知发布者”提示；请核验来源和 SHA-256，不要关闭系统安全保护。

发布目标为 Windows x64，主要面向 Windows 11 x64；未完整验证 Windows 版本和设备兼容矩阵。未提供 ARM64、Linux 或 macOS 构建。

1. 在“配置管理”添加 API Base URL、Key、协议与默认模型。Base URL 是 API 根地址，例如 `https://relay.example.com/v1`，不填具体请求端点，程序不自动补 `/v1`。
2. 确认接收方后拉取模型列表，或手填模型 ID；列表请求直接发往中转站，不经过 BazaarLink。
3. “本地配置检查”不联网；“测试连接”直接调用中转站。约 256 token 的合成英文提示含三个随机标记，独立校验结果不是模型真伪或能力评分，也不是 256k 长上下文测试。
4. 如需第三方鉴别，阅读外发确认后再提交。**这会将完整 Key、Base URL 与模型信息交给 BazaarLink**，建议使用低额度、可撤销的测试专用 Key。
5. 两类自动巡检单独选择范围并授权。最小化到托盘后仍运行；标题栏关闭或托盘“退出”才结束程序。

首次空态不自动联网。数据默认保存于 `%LOCALAPPDATA%\ProbeDesktop`，与程序目录分离；不要把运行数据放入云同步目录。可用 `--data-dir <目录>` 指定隔离数据目录。只支持直连公开 HTTPS；不读取系统代理，不提供自定义 HTTP 代理。

## 数据与安全

本地 Key 和运行历史使用 Windows 当前用户 DPAPI 加密。迁移电脑或用户环境请使用“帮助与设置”中的密码保护完整迁移包，不依赖直接复制状态文件。完整迁移包包含真实密钥，不是脱敏报告，不能公开分享。更多费用、授权和迁移边界见 [SAFETY.md](SAFETY.md)。

## 构建与回归

需要 Windows 和 **.NET 10 SDK**，在仓库根目录运行 PowerShell：

```powershell
.\build.ps1 -Task build
.\build.ps1 -Task test
.\build.ps1 -Task publish -SingleFile
```

若 `dotnet` 不在 PATH，向脚本传入 `-Dotnet <SDK可执行文件>`。首次还原和自包含发布可能联网访问官方 NuGet 源下载 SDK 所需组件；自动测试使用隔离目录和模拟 HTTP，不调用真实模型。

发布脚本先构建并运行控制台回归 runner（不是 `dotnet test`），再在 `artifacts/` 生成 EXE、ZIP 和 SHA-256 文件。每次使用新时间戳目录，已有产物不覆盖。产品版本 `0.9.1`，文件和程序集版本 `0.9.1.0`。

## 结构与限制

- `src/ProbeDesktop.Core/`：配置、存储、凭据保护、HTTP、安全与迁移。
- `src/ProbeDesktop.App/`：WPF 客户端与巡检界面。
- `tests/ProbeDesktop.Tests/`：自动回归测试。
- [RELEASE-NOTES.md](RELEASE-NOTES.md)：更新内容与验证边界。

真实中转站兼容性、三协议第三方鉴别完整流程、实际账单及长周期巡检未全面验证。软件不是 BazaarLink 官方产品，不代表其背书，不保证报告私密、远端取消或永久接口兼容。

官方参考：[Probe API](https://bazaarlink.ai/probe-api-skill.md)、[服务条款](https://bazaarlink.ai/terms)、[隐私政策](https://bazaarlink.ai/privacy)。本仓库未附加开源许可证；公开可见不等于授予任意复制、修改或再分发许可。
