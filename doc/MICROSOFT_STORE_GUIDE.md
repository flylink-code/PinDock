# PinDock 上架 Microsoft Store（Windows 应用商店）与 WinGet 指南

本文档说明如何将 PinDock 提交至 **Microsoft Store（微软应用商店）** 以及 **WinGet 官方软件源**。

---

## 一、准备工作：注册微软开发者账号（个人免费）

1. 访问 [Microsoft Partner Center (合作伙伴中心)](https://partner.microsoft.com/dashboard/windowsoverview) 并使用个人 Microsoft 账号登录注册（目前个人开发者免注册费）。
2. 在 **应用和游戏 -> 新产品 -> MSIX 或 PWA 应用**（或选择 **EXE 或 MSI 应用**）中预留应用名称 `PinDock`。
3. 进入 **产品管理 -> 产品标识 (Product Identity)** 页面，复制微软分配给你的 3 个参数：
   - `Package/Identity/Name`（例如：`12345YourName.PinDock`）
   - `Package/Identity/Publisher`（例如：`CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX`）
   - `Package/Properties/PublisherDisplayName`（你的开发者显示名称）

---

## 二、方式 1：生成 MSIX 包上传至微软商店（推荐，微软自动签名）

拿到上述 3 个标识参数后，在仓库根目录运行以下命令即可一键生成 `.msix` 上传包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-msix.ps1 `
  -IdentityName "你在PartnerCenter的IdentityName" `
  -PublisherId "CN=你在PartnerCenter的PublisherGUID" `
  -PublisherDisplayName "你的开发者名称"
```

- 产物路径：`dist\PinDock-0.1.5-x64.msix`
- 上传位置：在 Partner Center 的 **包 (Packages)** 页面直接拖入 `dist\PinDock-0.1.5-x64.msix` 即可。
- **隐私政策 URL（必填）**：直接填写本仓库的隐私声明地址：
  `https://github.com/flylink-code/PinDock/blob/main/PRIVACY.md`
- **提交审核说明 (Notes for certification)**：由于使用了 `runFullTrust`，在提交审核的备注框粘贴以下说明即可快速通过人工审核：
  > PinDock is a desktop floating dock and quick launcher utility for Windows. It requires the `runFullTrust` capability to register global hotkeys (`RegisterHotKey`), extract high-DPI shell icons (`SHGetFileInfo`), and launch user-bookmarked desktop applications and folders via Windows Shell.

---

## 三、方式 2：直接使用 `.exe` 安装器上架微软商店

如果你在 Partner Center 创建产品时选择了 **EXE 或 MSI 应用**：
1. **安装包 URL**：填写 GitHub Release 安装包直链：
   `https://github.com/flylink-code/PinDock/releases/download/v0.1.5/PinDock-Setup-0.1.5.exe`
2. **安装程序参数 (Installer parameters)**：填写 `/SILENT`
3. **隐私政策 URL**：填写 `https://github.com/flylink-code/PinDock/blob/main/PRIVACY.md`

---

## 四、同步发布至 Windows 官方 `winget` 软件源

每次发布新版本后，运行以下脚本即可自动计算 `dist\PinDock-Setup-<Version>.exe` 的 SHA256 并生成标准 WinGet 清单：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\generate-winget-manifest.ps1
```

生成的 YAML 清单位于 `installer\winget\flylink-code.PinDock\<Version>\`，可通过 `wingetcreate` 或 PR 提交至 `https://github.com/microsoft/winget-pkgs`。合并后用户即可通过命令安装：

```powershell
winget install flylink-code.PinDock
```
