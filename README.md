# 魂匣 SoulsVault

一个中文 Windows 桌面存档与简单 Mod 管理器，使用 **C# / WPF / .NET 8**。支持黑暗之魂 1（受死版、重制版）、2（原版、原罪学者）、3、艾尔登法环和只狼。

## 快速开始

1. 从 Releases 下载 `SoulsVault-win-x64.zip`，解压后双击 `SoulsVault.exe`。发行包自带 .NET 运行时。
2. 程序扫描当前 Windows 用户的常见存档目录。未找到时，点击“手动定位”。
3. 退出游戏，选择存档，填写备份名称，点击“提取并保存到备份库”。
4. 选择本地备份，可查看角色信息、导出文件，或替换指定游戏存档。替换前会自动保留旧文件。

“导入”把外部文件存入备份库；“下载”和“同步”也只写入备份库。只有“替换游戏存档”才会写入游戏正在使用的存档位置，需要在界面中确认。

## 游戏与 Mod 存档

| 游戏 | 原版文件 | 默认位置 |
| --- | --- | --- |
| 黑魂 1 受死版 | `DRAKS0005.sl2` | 文档目录下 `NBGI/DarkSouls` |
| 黑魂 1 重制版 | `DRAKS0005.sl2` | 文档目录下 `NBGI/DARK SOULS REMASTERED` |
| 黑魂 2 原版 | `DARKSII0000.sl2` | `%APPDATA%/DarkSoulsII` |
| 黑魂 2 原罪学者 | `DS2SOFS0000.sl2` | `%APPDATA%/DarkSoulsII` |
| 黑魂 3 | `DS30000.sl2` | `%APPDATA%/DarkSoulsIII` |
| 艾尔登法环 | `ER0000.sl2` | `%APPDATA%/EldenRing` |
| 只狼 | `S0000.sl2` | `%APPDATA%/Sekiro` |

扫描会进入账号子目录。相同主文件名的 `.co2` 会标记为联机 Mod 存档；其他合法自定义扩展名会显示为 Mod 存档。排除 `.bak`、`.tmp` 和复合备份后缀，并检查 BND3/BND4 容器头及大小。

识别基于文件名、目录及容器特征，不是完整的游戏格式验证器。使用同一个 `.sl2` 的 Mod 无法仅靠文件名与原版区分；请在备份名称中记录 Mod。受死版与重制版的外部文件重名时要求选择版本。更换了主文件名的存档需恢复原始主文件名后导入。

不同游戏、不同 Mod 扩展名不能相互替换。不会转换 Steam 账号绑定、拆分角色槽位、修改等级，也不会将联机 Mod 存档转换为原版。跨账号或跨游戏补丁版本是否能加载由游戏决定。

## 自选备份目录

默认库为 `%LOCALAPPDATA%/SoulsVault/vault`；检测到旧版 BonfireSave 配置时会继续使用旧目录。点击“选择备份目录”可切换至其他磁盘或已有备份库；原目录内容保留，不会自动搬迁或删除。若要迁移，先关闭程序，复制整个备份库，再选择新目录。

仓库链接、备份位置和手动定位的存档路径保存在数据目录的 `settings.json`。程序代码目录不保存个人存档。可以在 EXE 旁放置 `data-location.json`，例如 `{"DataDirectory":"D:\\SoftWareData\\SoulsVault"}`，把配置与备份统一放到指定目录。`SOULSVAULT_DATA_HOME` 环境变量可覆盖此设置，并兼容旧版 `BONFIRE_DATA_HOME`。

## GitHub 私有同步

联网功能使用本机 [GitHub CLI](https://cli.github.com/) 的当前身份。安装 `gh` 后执行 `gh auth login --hostname github.com --web`，或点击程序中的“GitHub 登录”在弹出的终端完成登录。已有登录无需重复；程序不要求输入或保存 Token。

仓库框支持：

```text
https://github.com/your-name/your-save-vault
https://github.com/your-name/your-save-vault.git
git@github.com:your-name/your-save-vault.git
your-name/your-save-vault
```

输入链接后点击“连接仓库”。也可填写尚不存在的仓库名，然后“新建私有仓库”。只支持 GitHub.com；SSH 链接作为仓库标识解析，实际请求仍通过 `gh` 的 GitHub API 身份发出。

- **上传选中备份**：上传一个不可变快照；同一 ID 重复上传会验证已有内容。
- **浏览 / 下载**：按保存时间和游戏类型选择云端备份，下载后校验 SHA-256。
- **双向同步全部备份**：将两侧缺少的快照补齐，不传播删除、不覆盖同名快照、不改游戏文件；失败后可重试，已完成的快照保留。

每次传输都会检查仓库仍为私有。同步协议为本工具的 `snapshots/<UTC时间>_<游戏类型>_<ID>.zip`，包内有 `manifest.json` 和 `save.sl2`；原始扩展名由游戏类型恢复。仓库中无关文件会忽略。当前不直接导入任意仓库中的散装存档文件，需下载到本地后用“导入存档”。云端目录显示游戏、时间、ID，完整名称在下载后显示。

私有库控制访问权限，不提供端到端加密。SHA-256 用于传输与备份完整性校验。单份原始存档上限 32 MB；GitHub 仓库容量和 API 配额依然适用。

## 角色信息（尽力解析）

“角色信息”读取当前选中的游戏存档；“查看角色信息”读取本地备份。解析完全在本机内存中进行，不写回游戏文件。

| 游戏 | 当前实现 |
| --- | --- |
| 法环 | 昵称、等级、游玩时间、地图 ID |
| 黑魂 3 | 昵称、等级（属性和与菜单等级交叉校验）、游玩时间、持有灵魂 |
| 黑魂 2 | 尝试解密昵称、等级、灵魂和游玩时间；部分联机版本布局不兼容 |
| 黑魂 1 | 尝试从角色结构与菜单交叉确认昵称、等级、游玩时间；旧版布局可能无法识别 |
| 只狼 | 游玩时间、攻击力、体力、金钱；游戏没有自定义角色昵称及魂系等级 |

内部条目校验失败、布局不匹配或无法确认的字段显示“未识别”。地图暂不提供完整中文地点名，也不推断精确坐标。已在本机真实法环、黑魂 3 联机和只狼存档上验证读取；没有对每个游戏版本完成游戏内加载测试。解析器参考和许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 简单 Mod 管理

点击“Mod 管理”自动扫描 Steam 库，也可选择游戏主程序定位目录。

- 识别常见 Seamless Co-op、Sekiro Online、Mod Engine、Elden Mod Loader、Dynamic Key Prompts 等标志文件。DLL 名称相同不一定是同一个 Mod，因此未知代理 DLL 仅显示为候选项。
- 版本从文件声明读取，或采用安装时填写的版本；无法确认时显示未知，不将游戏版本当成 Mod 版本。
- 从本地 ZIP 安装，保留安装包内相对路径。ZIP 内容必须可以直接放入游戏主程序所在目录；外面套了一层下载目录时请先重新打包。
- 安装前显示文件布局，备份即将覆盖的原文件，记录安装后 SHA-256。拒绝路径穿越、符号链接、大小写重复项、游戏主程序/Steam API/存档覆盖，以及与已管理 Mod 的文件冲突。
- 只自动移除由本工具安装的 Mod。移除时恢复原文件；文件被用户修改过则停止，避免丢失新改动。手工安装项按 Mod 作者说明处理。
- 安装记录、安装内容和原文件备份放在游戏目录 `.bonfire-mods`，移除后仍保留。中断记录可再次尝试移除恢复。不要随意删除该目录。

支持普通解压型 Mod，不执行 ZIP 中的脚本或安装程序，不解决 Mod 间兼容性，不自动从 Nexus 下载，不处理需要专用安装器/游戏参数合并的大型整合包。安装、移除和存档替换都要求游戏退出。

## 开发与验证

```powershell
dotnet build src/Bonfire.App/Bonfire.App.csproj -c Release
dotnet run --project tests/Bonfire.Tests -c Release
dotnet publish src/Bonfire.App/Bonfire.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/SoulsVault-win-x64
```

本地测试只在临时目录内生成模拟存档和 Mod 文件。可选的 GitHub 集成测试会向指定私有库上传模拟备份，验证往返与同步后删除当前分支上的模拟文件（Git 历史仍会保留测试提交）：

```powershell
dotnet run --project tests/Bonfire.Tests -c Release -- --cloud your-name/private-test-vault
```

只读诊断：`--inspect-local` 解析本机存档；`--inspect-mods` 列出已安装游戏的 Mod。诊断结果可能含角色昵称，不要直接提交到公开仓库。

持续集成模板见 `docs/github-actions-build.yml`。有 GitHub workflow 写入权限后，将其复制到 `.github/workflows/build.yml` 即可启用自动 Windows 构建；当前仓库未启用该工作流。

## 参考

- [WPF 官方说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [SoulsSpeedruns Save Organizer：存档目录](https://github.com/Kahmul/SoulsSpeedruns-Save-Organizer)
- [Seamless Co-op：自定义存档扩展名](https://ersc-docs.github.io/how-to-install-and-update/)

代码按 MIT 许可发布；衍生解析代码保留 Apache-2.0 归属和许可。与 FromSoftware、Bandai Namco、Steam 或 Mod 作者无关联。
