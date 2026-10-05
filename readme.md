# MobilePackageGen - 移动设备包生成工具

基于 [WSK Tools](https://github.com/gus33000/WSK) v1.0.4 独立重构的版本（fork 自上游 [MobileTooling/MobilePackageGen](https://github.com/MobileTooling/MobilePackageGen)），支持从 FFU / VHDX / VHD / 分区中提取 CBS、SPKG、Driver 包。

## 项目结构

```
MobilePackageGen/
├── MobilePackageGen.sln                     解决方案文件
├── src/                                     第一方代码
│   ├── MobilePackageGen.CLI/                命令行版本 (net8.0)
│   ├── MobilePackageGen.GUI/                GUI 版本 (net8.0-windows, WinForms)
│   ├── MobilePackageGen.Common/             公共核心库
│   ├── MobilePackageGen.Adapters/           镜像读取适配器 (FFU/VHDX/WIM/目录)
│   ├── Archives.DiscUtils/                  DiscUtils 扩展 (含 7z.dll / SevenZipExtractor)
│   ├── Img2Ffu.Library/                     FFU 镜像读写库（本地版）
│   ├── Img2Ffu.Library.Compression.Windows/ Windows 压缩实现
│   ├── StorageSpace/                        OSPool/Storage Spaces 解析库（本地版）
│   ├── LibSxS/                              SxS Delta 清单解析库
│   ├── Microsoft.Deployment.Compression/    CAB 压缩基础库 (来自 WiX)
│   └── Microsoft.Deployment.Compression.Cab/ CAB 压缩实现
└── thirdparty/                              第三方库
    ├── DotnetPackaging.Msix/                MSIX/Appx 打包库
    ├── DeflateBlockCompressor/              Deflate 块压缩库
    ├── StorageSpace/                        子模块（上游引用，见 .gitmodules）
    ├── Img2Ffu/                             子模块（上游引用，见 .gitmodules）
    └── EDLProgram2VHDX/                     子模块（上游引用，见 .gitmodules）
```

## 编译方法

### 命令行版本

```bash
dotnet build src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release
```

### GUI 版本

```bash
dotnet build src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release
```

### 发布 (self-contained, 免安装运行库)

```bash
# amd64
dotnet publish src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-x64 --self-contained true -o output/amd64
dotnet publish src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-x64 --self-contained true -o output/amd64

# x86
dotnet publish src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-x86 --self-contained true -o output/x86
dotnet publish src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-x86 --self-contained true -o output/x86

# arm64
dotnet publish src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-arm64 --self-contained true -o output/arm64
dotnet publish src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-arm64 --self-contained true -o output/arm64
```

## 使用方法

### 命令行版本

```bash
# 从分区提取 CBS 包
MobilePackageGen.exe <Path to MainOS/Data/EFIESP> <Output folder CBSs>
# 示例: MobilePackageGen.exe D: E: F: C:\OutputCabs

# 从 FFU 文件提取
MobilePackageGen.exe <Path to FFU File> <Output folder CBSs>
# 示例: MobilePackageGen.exe D:\Flash.ffu C:\OutputCabs

# 从 VHDX 提取
MobilePackageGen.exe <Path to VHDx> <Output folder CBSs>
```

> 注意：部分功能需要以 Trusted Installer (TI) 权限运行。

### GUI 版本

直接运行 `MobilePackageGen.GUI.exe`，通过图形界面操作。

## 第三方组件与来源

| 组件 | 来源 | 许可证 | 本仓库位置 |
|---|---|---|---|
| LTRData.DiscUtils | [LTRData/DiscUtils](https://github.com/LTRData/DiscUtils)（NuGet 1.0.85） | MIT | NuGet 包 |
| Img2Ffu | [WOA-Project/Img2Ffu](https://github.com/WOA-Project/Img2Ffu) | MIT | `src/Img2Ffu.Library`（本地版，含压缩实现） |
| StorageSpace | [gus33000/StorageSpace](https://github.com/gus33000/StorageSpace) | MIT | `src/StorageSpace`（本地版）；`thirdparty/StorageSpace` 保留子模块声明 |
| DotnetPackaging.Msix | [dotnet-packaging/MSIX](https://github.com/dotnet-packaging/MSIX) | MIT | `thirdparty/DotnetPackaging.Msix` |
| DeflateBlockCompressor | 随 WSK Tools 分发 | - | `thirdparty/DeflateBlockCompressor` |
| Microsoft.Deployment.Compression | WiX 工具集 | MS-RL | `src/Microsoft.Deployment.Compression(.Cab)` |
| LibSxS | 随 WSK Tools 分发 | - | `src/LibSxS` |
| SevenZipExtractor / 7-Zip | [adoconnection/SevenZipExtractor](https://github.com/adoconnection/SevenZipExtractor) | LGPL-2.1 | `src/Archives.DiscUtils/SevenZipExtractor` |
| RawDiskLib | NuGet 0.2.1 | - | NuGet 包 |
| EDLProgram2VHDX | [gus33000/EDLProgram2VHDX](https://github.com/gus33000/EDLProgram2VHDX) | - | `thirdparty/EDLProgram2VHDX`（子模块声明） |

> **子模块说明**：`thirdparty/StorageSpace`、`thirdparty/Img2Ffu`、`thirdparty/EDLProgram2VHDX` 在 `.gitmodules` 中保留了上游的子模块引用声明（fork 结构沿用上游）。本仓库**实际编译使用的是 `src/` 下的本地代码**（StorageSpace、Img2Ffu.Library），因此克隆后**不需要**初始化子模块即可直接编译。

## 与上游 fork 的主要差异

- 代码结构重组为 `src/` + `thirdparty/` 布局（保留上游组织方式）
- 新增 `MobilePackageGen.GUI`（WinForms 图形界面）与 `MobilePackageGen.CLI` 分离
- `LTRData.DiscUtils` 升级至 1.0.85（修复 WOF/XPRESS 压缩文件读取导致的数组越界崩溃）
- 镜像加载支持目录（RealFileSystem）方式，绕过 WOF 解码路径

## 版本信息

- 来源: WSK Tools v1.0.4（fork 自 MobileTooling/MobilePackageGen）
- 导出日期: 2026-08-30
- 项目数: 13（src/ 11 + thirdparty/ 2）
- CLI 目标框架: net8.0
- GUI 目标框架: net8.0-windows (WinForms)
