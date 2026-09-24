# SourceManager (`sm`)

SourceManager 是一套全新的版本控制系统，从第一行代码起就按自己的契约设计，而不是复刻 Git 的历史包袱。
它由两部分组成：

- **`LibSM`** —— 版本控制引擎（对象存储、索引、引用、合并、reflog、传输协议）。
- **`sm`** —— 构建在引擎之上的命令行前端（porcelain / plumbing）。

> 本 README 是项目的**入口与快速上手**。设计立场、契约取舍与逐条决策记录在
> [`DESIGN-STANCE.md`](DESIGN-STANCE.md)；两者的定位不同，请勿混用。

---

## 特性

- **SHA-256 内容寻址**：从第一天起使用强哈希，不背 SHA-1 迁移债。
- **自研传输协议**：二进制 `sm://` 协议（magic `SMP1`），带版本协商；同时复用 `file` / `http(s)` / `ssh` 通道。
- **JSON 是一级公民**：`--json` 是跨命令的统一契约，机器可读输出无需解析 fragile 的文本格式。
- **完整 reflog**：`commit` / `checkout` / `reset` / `merge` / `rebase` / `branch` / `tag` / `fetch` / `pull` 等操作全程记账，并支持 `<ref>@{n}`、`<ref>@{<time>}`、`@{-n}` 版本语法。
- **单文件发布**：唯一 NuGet 依赖 `System.IO.Hashing`，部署简单可控。
- **现代运行时**：.NET 10 / C# `LangVersion 13`。

---

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- 运行回归套件需要 PowerShell 7+（`pwsh`）

## 构建

```pwsh
dotnet build SourceManager.csproj
```

产物位于 `bin/Debug/net10.0/win-x64/sm.exe`。

发布单文件版本：

```pwsh
dotnet publish SourceManager.csproj -c Release
```

## 快速开始

```pwsh
# 初始化仓库（.sm 目录）
sm init

# 暂存并提交
sm add -A
sm commit -m "Initial commit"

# 查看状态与历史
sm status
sm log --oneline --graph

# 分支
sm branch feature
sm switch feature

# 机器可读输出
sm status --json
sm log --json
```

---

## 命令一览

| 分类 | 命令 |
|---|---|
| 仓库与维护 | `init` `config` `gc` `fsck` `verify` `serve` |
| 暂存与提交 | `add` `commit` `status` `diff` `rm` `mv` `clean` |
| 历史与分支 | `log` `branch` `checkout` `switch` `merge` `rebase` `cherry-pick` `revert` `reset` `stash` `tag` |
| 远程 | `remote` `fetch` `pull` `push` |
| 查询与调试 | `show` `blame` `bisect` `describe` `shortlog` `merge-base` `reflog` |
| Plumbing | `rev-parse` `cat-file` `hash-object` `ls-files` `ls-tree` `archive` |
| 帮助 | `help` `version` |

常用别名：`co` → `checkout`，`ci` → `commit`，`st` → `status`，`br` → `branch`，`cp` → `cherry-pick`，`rb` → `rebase`。

查看某个命令的用法：

```pwsh
sm help <command>
```

## JSON 输出

在任意命令后追加 `--json` 即可获得机读文档：

```pwsh
sm log -n 10 --json
sm status --json
sm fsck --json
```

## 测试

```pwsh
pwsh tests/run-tests.ps1
```

套件会针对临时目录中的一次性仓库运行真实的 `sm` 二进制，并断言可观测行为（退出码、stdout/stderr、仓库状态）。

---

## 文档

| 文档 | 内容 |
|---|---|
| `README.md`（本文件） | 项目入口、构建、快速上手 |
| [`DESIGN-STANCE.md`](DESIGN-STANCE.md) | 设计立场与契约决策（与 Git 的差异、待定项、实现记录） |
| [`docs/index.html`](docs/index.html) | 项目文档站点 |
| `sm help <command>` | 各命令的即时用法 |
