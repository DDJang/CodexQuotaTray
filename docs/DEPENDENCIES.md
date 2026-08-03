# Dependency and license inventory

状态：当前 WinUI 3 构建
最后更新：2026-07-29

## 工具链

- .NET SDK `10.0.302`，由仓库根目录 `global.json` 固定；
- Windows x64 / `win-x64`；
- Inno Setup 7，仅用于生成 per-user 安装器。

## 直接 NuGet 依赖

版本由 `winui/Directory.Packages.props` 固定：

| Package | Version | 用途 |
|---|---:|---|
| Microsoft.WindowsAppSDK | 2.2.0 | WinUI 3、窗口与 Windows App SDK 运行时 |
| CommunityToolkit.Mvvm | 8.4.2 | Core view model 与 MVVM 基础 |
| MSTest.Sdk | 4.3.2 | 测试项目 SDK |

自包含发布目录还包括 .NET runtime、Windows App SDK 的 native/runtime 文件及其传递依赖。实际发布许可证集合应以干净 restore 后的 `project.assets.json`、NuGet 包许可证和最终 publish 内容为准。

## 产品边界

- 应用不嵌入浏览器 UI，不使用 WebView 展示额度；
- 不引入遥测后端或独立账户服务；
- `codex app-server` 是由应用启动和回收的外部受控子进程；
- 旧 Rust/Cargo 依赖不属于当前构建，其完整历史位于 `archive/rust-win32-final`。

## 发布要求

- 发布前审查所有直接和传递依赖的许可证；
- 自包含目录中的许可证材料必须与实际打包内容一致；
- 签名凭据不得进入仓库；
- 安装器直接读取 `target/winui-publish/`，不以便携 ZIP 为输入。
