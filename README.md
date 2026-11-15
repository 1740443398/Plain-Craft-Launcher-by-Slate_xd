# Plain Craft Launcher by Slate_xd · 源码

> 这是**唯一权威源码**。其他目录下的副本（`gh-repo/EXE/`、`site/EXE/`）都是旧镜像，不要从那里改。

---

## 基本信息

| 项 | 值 |
|---|---|
| 语言 | VB.NET + WPF |
| 目标框架 | .NET Framework 4.8 |
| RootNamespace | `PCL` |
| AssemblyName | `Plain Craft Launcher by Slate_xd` |
| 工程文件 | `Plain Craft Launcher by Slate_xd.vbproj` |
| 规模 | 71 个 XAML + 157 个 VB |

---

## 构建

```powershell
$src = "E:\蕭遞\PCL3\source_final\opensource-2.13.1.1\Plain Craft Launcher by Slate_xd"
$msb = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
$ros = "E:\蕭遞\PCL3\_rea\tools\roslyn-toolset\tasks\net472"
$o   = "E:\蕭遞\PCL3\_rea\out_my\"

if (Test-Path $o) { Remove-Item $o -Recurse -Force -EA SilentlyContinue }

& $msb "$src\Plain Craft Launcher by Slate_xd.vbproj" /t:Rebuild `
  /p:Configuration=Release /p:Platform=AnyCPU /p:OutputPath="$o" `
  /p:VbcToolPath="$ros\" /p:VbcExe=vbc.exe `
  /p:CscToolPath="$ros\" /p:CscToolExe=csc.exe `
  /p:LangVersion=latest /v:minimal /nologo
```

**为什么需要 Roslyn**：项目用 `LangVersion=latest`，.NET Framework 自带的旧 `vbc.exe` 不支持部分语法。Roslyn 工具集在 `_rea\tools\roslyn-toolset\`。

**耗时**：约 2-4 分钟。

**并行构建**：多个构建同时跑会抢 `obj\Release`，报 `BC2012`。加 `/p:BaseIntermediateOutputPath="$o\obj\"` 隔离。

---

## 目录结构

```
Plain Craft Launcher by Slate_xd/
├── Application.xaml              ← 全局资源 + 18 个设计令牌
├── FormMain.xaml(.vb)            ← 主窗口（2163 行）
├── Controls/                     ← 自定义控件（40+ 个）
│   ├── MyGlass.vb                ← 毛玻璃（450 行）
│   ├── MyStepBar.vb              ← 步骤条（156 行）
│   ├── MyLiquidIndicator.vb      ← 液态 Tab 指示器（143 行）
│   ├── MyCard.vb                 ← 卡片
│   ├── MyButton.xaml.vb          ← 按钮
│   ├── MyListItem.xaml.vb        ← 列表项（Logo 渲染在这里）
│   ├── MyScrollViewer.vb         ← 滚动容器（AllowBubbleToParent）
│   └── ...
├── Modules/
│   ├── ModServer.vb              ← 开服器核心（938 行）
│   ├── ModServerConfig.vb        ← 配置/备份/白名单（1136 行）
│   ├── ModBedrock.vb             ← 基岩版（820 行）
│   ├── ModPluginStore.vb         ← 插件广场（639 行）
│   ├── ModTerracotta.vb          ← 陶瓦联机（469 行）
│   ├── Base/ModAnimation.vb      ← 动画引擎（AniStart / AaOpacity / 缓动）
│   ├── Base/ModSecret.vb         ← 主题色（HSL 驱动）
│   └── ...
├── Pages/
│   ├── PageServer/               ← ⚠️ 开服页（UI 已回退，需重做）
│   ├── PageLaunch/               ← 启动页（含基岩版按钮）
│   ├── PageOther/                ← 更多（含鸣谢页 PageOtherAbout）
│   ├── PageDownload/             ← 下载页
│   ├── PageSetup/                ← 设置页
│   └── ...
├── Images/
│   ├── Heads/                    ← 20 个头像（全部 64×64）
│   └── Blocks/                   ← 帮助页图标（64×64）
├── Resources/
│   └── Custom.xaml
└── Plain Craft Launcher by Slate_xd.vbproj
```

---

## 关键设计

### 设计令牌

`Application.xaml` 顶部注入 18 个令牌：

```xml
Radius.Small=4  Radius.Card=8  Radius.CardLarge=12  Radius.Pill=999
Font.Caption=11  Font.Body=12  Font.Emphasis=14  Font.Subtitle=16  Font.Title=20
Gap.Card / Gap.CardInner / Gap.Item / Gap.Tight / Gap.Block
Size.Button=32  Size.ButtonSmall=28  Size.Input=32  Size.ListItem=36
```

用法：`CornerRadius="{StaticResource Radius.Card}"`

### 主题色

`ModSecret.vb` 里 `ColorHue=210, ColorSat=85`，颜色自动派生（HSL）。改主题色不需要改硬编码。

可用资源：`ColorBrush1`..`ColorBrush8`、`ColorBrushBg0/1`、`ColorBrushGray1`..`ColorBrushGray3`、`ColorBrushSemiTransparent`

### 动画

`Modules\Base\ModAnimation.vb`：

```vb
' 淡入
AniStart({AaOpacity(元素, 增量, 时长, 延迟, 缓动)}, "动画名")
' 数值
AniStart({AaDouble(元素, 属性, 增量, 时长, 延迟, 缓动)}, "动画名")
' 缩放
AniStart({AaScaleTransform(元素, 值, 时长, 延迟, 缓动)}, "动画名")
```

**缓动**：`AniEaseOutFluent(AniEasePower.Weak)`（进入）、`AniEaseInFluent(...)`（退出）、`AniEaseInoutFluent(...)`（屏内移动）

**同名动画会自动替换旧的**，所以连点不会叠加。

---

## ⚠️ 修改前必读

### 铁律

1. **每次改完立刻构建**，不要连改多处
2. **大改之前先备份**（项目没有 git，见下）
3. **不要用脚本批量改 .vb**（VB 语法坑极多，正则替换易破坏结构）

### 建 git 仓库（第一件事）

```bash
cd "E:\蕭遞\PCL3\source_final\opensource-2.13.1.1\Plain Craft Launcher by Slate_xd"
git init
# 先写 .gitignore：
#   obj/
#   bin/
#   *.bak_*
git add -A
git commit -m "baseline"
```

### 编码要求

**所有 `.vb` 文件必须 UTF-8 BOM。** `edit` 工具会剥掉 BOM，改完要补：

```powershell
[System.IO.File]::WriteAllText($path, $content, (New-Object System.Text.UTF8Encoding($true)))
```

---

## VB.NET 语法坑（踩过就懂）

| 坑 | 说明 |
|---|---|
| 注释不能写在数组初始化器 `{...}` 内 | 编译失败 |
| 多行字符串字面量编译成空串 | 用 `"行1" & vbCrLf & "行2"` |
| 字符串里不能用全角引号 `「」` | 编译失败 |
| 单行 lambda 不能写 `Return` | 必须多行 |
| `Step` / `In` / `Out` 是关键字 | 不能作变量名 |
| `obj.Event += handler` 无效 | 用 `AddHandler obj.Event, handler` |
| `IsRunning` 在多个 Module 有声明 | 写 `ModServer.IsRunning` |

## XAML / WPF 坑

| 坑 | 说明 |
|---|---|
| 页面级 `BitmapScalingMode="LowQuality"` 覆盖 Style 的 `HighQuality` | 图标变形的真凶 |
| `Canvas.Background` 的 ImageBrush 默认 `Stretch=Fill` | 非正方形图被压扁 |
| 像素风图标要用 `NearestNeighbor` | `Linear` 会糊 |
| `MyScrollViewer.PreviewMouseWheel` 无条件 `Handled=True` | 嵌套滚动要 `AllowBubbleToParent=True` |
| `MyButton` 用 `Click` 事件 | 不是 `MouseLeftButtonUp` |
| 浮点误差 `-4.3E-14` 喂给 Width 会崩 | `Math.Round(x, 1)` + 保证非负 |

---

## 当前待办

见根目录 [`HANDOVER.md`](../../../HANDOVER.md) §9。

最紧急：**开服页向导重做**（`Pages/PageServer/`）。
