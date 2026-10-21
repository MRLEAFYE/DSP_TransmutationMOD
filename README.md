# 转化桌 (Transmutation Table)

戴森球计划 **ProjectE 风格的 EMC 等价交换** Mod。

把任何物品折算成统一的能量货币 **EMC**，再用 EMC 兑换成你需要的任何物品。EMC 余额与配置随存档保存、全宇宙共享 —— 所有转化建筑共用同一个 EMC 空间，拆掉建筑也不会丢数据。

## 三种建筑

| 建筑 | 外观 | 用法 | 配方（模板原版配方 ×3） | 解锁科技 |
|---|---|---|---|---|
| **转化桌** | 分馏塔造型 | 三条传送带端口：输入带任意物品折算 EMC，输出带按配置生成物品 | 钢材×24 + 石材×12 + 玻璃×12 + 处理器×3 | 高效物流系统 |
| **转化箱** | 小型储物仓造型 | 分拣器送入即折算 EMC；可设一种输出物品自动备货，分拣器取走即补 | 铁块×12 + 石材×12 | 基础物流系统 |
| **转化站** | 行星内物流站造型 | 输入端口自动收购全星球物流塔供应的物品；输出端口用 EMC 供货，自带无人机派送 | 钢材×120 + 钛块×120 + 处理器×120 + 粒子容器×60 | 星际物流系统 |

三座建筑放在建造菜单 **「仓储」分类，储液罐后面三格**。

## 学习机制（重要）

兑换不是一开始就全开的：

- **物品必须被投入过一次**，才会解锁兑换（传送带喂进转化桌、送进转化箱、直插转化站、界面里"存入背包物品"都算）；
- 未学习的物品在取物列表里压暗显示"未学习"，悬停会提示怎么解锁；
- 界面顶部显示当前已学习数量；
- **沙盒模式**下界面顶部有「一键解锁全部物品」按钮，点一下全部开放。

## 界面操作

对着建筑按 **E** 或用鼠标点击打开界面（DSP 原生风格，可拖动、右下角可缩放）：

- 左键取 1 个、Shift+左键取 1 组、右键取 10 个
- 「存入背包中全部物品」按 EMC 折算一次清空
- 输出参数：输出速度（1~60 个/秒）、输出喷涂等级（MK.I~III，按涂料成本加价）
- 转化站：开关自动收购、配置供应物品、无限小飞机

## EMC 定价

- 基础矿一律 ×10，水 5，沙土 1；
- 其他物品按**最贵的低效配方**反推（走高效配方生产才有差价可赚）；
- 价格写死在文本文件里，可自行修改：

```
BepInEx/config/TransmutationTable.prices.txt
格式：物品ID EMC值 物品名（# 开头为注释）
```

改完重启游戏生效；文件里没有的物品按配方自动推导。

## 依赖

- BepInEx 5.4.x
- LDBTool 3.0.x
- DSPModSave（存档数据）

## 说明

- 与任何注入原型的 Mod 一样，原版的"数据异常"签名校验会因原型表变化而误报，本 Mod 已做签名自洽处理（不影响存档与玩法）；
- 不建议与其它大幅改动原版物流/物品数值的 Mod 同时使用，EMC 定价会随之变化。

## 安装

**推荐**：通过 Thunderstore / 任意 Mod 管理器安装（会自动处理依赖）。

手动安装：

1. 确认已装 BepInEx 5.4.x、LDBTool、DSPModSave；
2. 把 `TransmutationTable.dll` 放进 `BepInEx/plugins/TransmutationTable/`；
3. 启动游戏。

## 从源码构建

需要 .NET SDK（能编译 net472）以及本地已装好游戏与 BepInEx 环境。依赖 DLL 通过三个环境变量定位：

| 变量 | 含义 | 示例 |
|---|---|---|
| `DYSON_SPHERE_PROGRAM_DIR` | 游戏根目录 | `E:\SteamLibrary\steamapps\common\Dyson Sphere Program` |
| `BEPINEX_CORE_DIR` | BepInEx 核心目录（含 BepInEx.dll、0Harmony.dll） | `...\BepInEx\core` |
| `BEPINEX_PLUGINS_DIR` | 插件目录（需含 `LDBTool\`、`DSPModSave\` 子目录） | `...\BepInEx\plugins` |

PowerShell 里设置后编译：

```powershell
$env:DYSON_SPHERE_PROGRAM_DIR = "E:\SteamLibrary\steamapps\common\Dyson Sphere Program"
$env:BEPINEX_CORE_DIR = "$env:APPDATA\Thunderstore Mod Manager\DataFolder\DysonSphereProgram\profiles\DJJH\BepInEx\core"
$env:BEPINEX_PLUGINS_DIR = "$env:APPDATA\Thunderstore Mod Manager\DataFolder\DysonSphereProgram\profiles\DJJH\BepInEx\plugins"
dotnet build -c Release
```

产物在 `bin/Release/TransmutationTable.dll`。

## 价格表

`prices.example.txt` 是价格表模板，复制到 `BepInEx/config/TransmutationTable.prices.txt` 即可覆盖默认定价：

```
物品ID  EMC值  物品名      # 制表符或空格分隔，# 开头为注释
```

## 目录结构

```
src/                  源码（EmcSystem 定价与存档、EmcWindow IMGUI 界面、TablePatches 建筑逻辑、
                      ProtoGuard 原型索引自愈、BuildMenuInstaller 建造菜单、PurpleModelTint 模型染色等）
manifest.json         Thunderstore 包元数据
CHANGELOG.md          版本记录
prices.example.txt    价格表模板
```

## 许可

MIT
