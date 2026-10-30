# PinDock 上架 Microsoft Store（Windows 应用商店）与 WinGet 指南

本文档说明如何将 PinDock 提交至 **Microsoft Store（微软应用商店）** 以及 **WinGet 官方软件源**。

---

## 一、准备工作：注册并开通「应用和游戏」工作区（个人免费）

如果你刚用微软账号登录 [Microsoft Partner Center (合作伙伴中心)](https://partner.microsoft.com/dashboard/home)，主页的「工作区」里**只有一个蓝色的 `＋` 号（「我的访问权限」）**，没有看到「应用和游戏」，说明你的账号还没有开通微软商店（Windows 和 Xbox）开发者计划，请按以下步骤开通：

### 1. 开通「Windows 和 Xbox」开发者计划
1. **方式 A（在合作伙伴中心内操作）**：
   - 点击主页「工作区」里的蓝色 **`＋` 号卡片（我的访问权限）** 进入「帐户设置 | 我的访问权限」页面；
   - 点击页面中间「快速入门」下的第 3 张卡片 **「注册计划」**，或者点击左侧导航栏第 4 项 **「程序」**（即 Programs）；
   - 在展开的列表中找到 **「Windows 和 Xbox」（Microsoft Store）**，点击右侧的 **「开始使用」 (Get started)**。
2. **方式 B（如果方式 A 列表里没看到 Windows 和 Xbox，直接走官方商店注册通道）**：
   - 直接打开微软商店开发者免费注册入口：https://storedeveloper.microsoft.com/ （或 https://partner.microsoft.com/dashboard/registration/windows ）；
   - 点击页面上的 **Sign up / 立即免费注册**，使用同一个个人 Microsoft 账号登录。
3. **填写开发者信息（0 元免费）**：
   - **账户类型**：选择 **个人 (Individual)**（个人开发者免费，无需公司营业执照）；
   - **国家/地区**：选择 **中国 (China)**；
   - **发布者显示名称 (Publisher display name)**：填写你想在微软商店展示的开发者名字（例如 `flylink` 或 `flylink-code`）；
   - 按页面提示完成手机/身份验证即可。

### 2. 进入「应用和游戏」工作区并创建应用
1. 注册完成后，重新打开或刷新 [合作伙伴中心主页](https://partner.microsoft.com/dashboard/home)（或直接访问 [Windows 概览页](https://partner.microsoft.com/dashboard/windowsoverview)），此时「工作区」下方会出现 **「应用和游戏 (Apps and games)」** 卡片。
2. 点击进入 **「应用和游戏」**，点击左上角的 **「＋ 新产品 (New product)」**：
   - 如果准备走 **方式 1（上传 `.msix` 包）**：选择 **「MSIX 或 PWA 应用」**，输入 `PinDock` 并点击 **预留产品名称 (Reserve product name)**。
   - 如果准备走 **方式 2（直接填 GitHub `.exe` 链接，最省事）**：选择 **「EXE 或 MSI 应用」**，输入 `PinDock` 预留名称后直接跳到下方「三、方式 2」。
3. （仅方式 1 需要）创建 MSIX 应用后，点击左侧菜单 **产品管理 (Product management) -> 产品标识 (Product Identity)**，复制微软分配给你的 3 个参数：
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

- 产物路径：`dist\PinDock-0.1.6-x64.msix`
- 上传位置：在 Partner Center 的 **包 (Packages)** 页面直接拖入 `dist\PinDock-0.1.6-x64.msix` 即可。
- **隐私政策 URL（必填）**：直接填写本仓库的隐私声明地址：
  `https://github.com/flylink-code/PinDock/blob/main/PRIVACY.md`
- **提交审核说明 (Notes for certification)**：由于使用了 `runFullTrust`，在提交审核的备注框粘贴以下说明即可快速通过人工审核：
  > PinDock is a desktop floating dock and quick launcher utility for Windows. It requires the `runFullTrust` capability to register global hotkeys (`RegisterHotKey`), extract high-DPI shell icons (`SHGetFileInfo`), and launch user-bookmarked desktop applications and folders via Windows Shell.

---

## 三、方式 2：直接使用 `.exe` 安装器上架微软商店

如果你在 Partner Center 创建产品时选择了 **EXE 或 MSI 应用**：
1. **安装包 URL**：填写 GitHub Release 安装包直链：
   `https://github.com/flylink-code/PinDock/releases/download/v0.1.6/PinDock-Setup-0.1.6.exe`
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
