# 开发指南

[首页](../README.md) · [工作原理](how-it-works.md) · [测试范围](../TESTING.md)

## 环境与构建

Windows x64、PowerShell 7、.NET 10 SDK。脚本使用新版 .NET 路径 API，不以 Windows PowerShell 5 为兼容目标。若仓库的 `.tools/dotnet` 中存在便携 SDK，构建脚本优先使用它；否则使用 PATH 中的 `dotnet`。无需下载或提交本机 SDK。

```powershell
./build.ps1
./build.ps1 -Test
```

发布为 Windows x64 自包含目录。脚本保留默认开发输出，但发行建议使用下面的隔离目录；不要覆盖正在运行的发行文件。打包前检查发行说明、许可和文档，不把私有诊断、恢复文件或临时联调截图加入归档。

CI 或隔离发布可使用独立构建目录，避免覆盖正在运行的开发版本：

```powershell
./build.ps1 -PureTest -BuildDir artifacts/ci-build
./build.ps1 -PureTest -Publish -BuildDir artifacts/release-build -PublishDir artifacts/release-publish
./scripts/package-release.ps1 -PublishDir artifacts/release-publish -OutputDir artifacts/release-assets -Version 0.1.1
```

`-PureTest` 与 `-Test` 互斥。前者仅运行无窗口纯策略子集，适用于 CI；后者保留完整交互式夹具。需要本地完整验证时将发布命令中的 `-PureTest` 换成 `-Test`。打包脚本使用公开文件允许列表，包含清理后的文档、手机截图和图标，使相对链接可在发行目录读取；不把本地历史或诊断目录加入发行包。

## 手动预发布

分支推送运行 Windows CI；推送版本标签会另行触发打包与发布。自动工作流未运行或未完成时，可以使用本地已验证、与标签源码一致的归档手动发布；不要把本地结果表述为 CI 通过。

先确认目标提交、归档与校验文件，再创建并推送标签；如果标签已存在，核对其提交，不覆盖已有标签：

```powershell
git tag v0.1.1
git push origin v0.1.1
```

推送标签会尝试触发发行工作流；若没有生成发行结果，可以使用 GitHub CLI 手动创建同一预发布。将下列 `<ZIP>` 与 `<SHA256SUMS>` 替换为本次打包输出的真实文件路径：

```text
gh release create v0.1.1 <ZIP> <SHA256SUMS> --verify-tag --prerelease --notes-file release-notes/v0.1.1.md
```

执行前检查是否已存在该版本，避免与工作流重复创建或覆盖资产。发布后核对版本标记、预发布状态、资产及 SHA-256 校验文件。

## 代码结构

| 路径 | 用途 |
| --- | --- |
| `src/PocketDesk/MainWindow*` | 应用库、设置、接入与布局编排 |
| `src/PocketDesk/ToolbarWindow.cs` | 应用 / 操作控制条 |
| `src/PocketDesk/Core` | 纯策略、设备资料、偏好、会话与恢复 |
| `src/PocketDesk/Native` | 显示、窗口、图标、UIA 与输入互操作 |
| `tests/PocketDesk.Tests` | 无第三方测试框架的 console runner 与专用 WPF 夹具 |
| `tools/ui-preview` | 真实 XAML 的独立布局预览 |
| `tools/icon-generator` | 程序图标生成源码 |

## 测试

完整 `-Test` 需要交互式 Windows 桌面：测试会创建、移动并关闭自己的夹具窗口。纯数据 CI 可运行以下两组，不执行原生窗口夹具：

```powershell
dotnet run --project tests/PocketDesk.Tests/PocketDesk.Tests.csproj -c Release -- --geometry-tests
dotnet run --project tests/PocketDesk.Tests/PocketDesk.Tests.csproj -c Release -- --layout-tests
```

这两个入口只运行子集，不能把 CI 通过等同于完整回归或手机实机验收。独立编译核心与原生源码可加 `-p:NativeOnly=true`；通常开发优先使用默认产品引用。

布局预览示例：

```powershell
dotnet run --project tools/ui-preview -c Release -- --all --frame-supported --output artifacts/ui-review/frame-swapped
dotnet run --project tools/ui-preview -c Release -- --all --frame-supported --toolbar --output artifacts/ui-review/frame-swapped/toolbar
```

预览使用虚拟数据和空回调，不创建真实产品会话或变更显示模式。请同时查看报告与图像；首行需要滚动的警告不能自动宣称为零问题。更多选项见仓库中的[预览工具说明](https://github.com/Jie-su/PocketDesk/blob/main/tools/ui-preview/README.md)。

## 修改原则

涉及显示或恢复时，优先添加纯策略和专用夹具回归：精确模式缺失必须停止，外部 PC 接管必须保留，失败必须保留日志，重复旋转不能覆盖原始快照。仅有模式宽高不能证明新的控制端身份。

不要在自动测试中更改真实显示模式、移动任意用户窗口或发送真实应用按键。新原生写入应经过明确的实机测试，并将它与空回调 UI 检查分开记录。

提交问题 / PR 时说明触发条件、预期行为、验证方式及尚未覆盖范围。诊断和截图先脱敏；`artifacts/`、`.tools/`、`bin/`、`obj/` 不应进入版本控制。
