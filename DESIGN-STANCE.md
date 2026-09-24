# SourceManager 设计立场

> 目的:把 `sm` 的设计选择写在**自己的契约**上,而不是默认继承 Git 的先验。
> 评估准则:先问「`sm` 自己的契约是什么」,而不是「Git 怎么做」。
>
> 状态标记:
> - `[已定]` 已实现并视为承诺
> - `[待定]` 需要拍板
> - `[拒绝]` 明确不做
>
> 本文件是活文档,逐条由作者确认。

---

## 0. 总原则

1. **工具的设计选择是设计空间里的一格,不是普世原则。** "分布式""不可变历史""功能分支""暂存区"都是可选项,不是公理。
2. **不为兼容 Git 而兼容 Git。** 只在能带来明确收益时采用 Git 的解法;否则按 `sm` 自身目标设计。
3. **对抗叙事惯性。** 任何"业界都这么做"的论据,先降级为"一种做法",再决定要不要。
4. **偏离要显性化。** 当参考 Git 时,主动标注"这是 Git 的先验,未必适用 `sm`"。

---

## 1. 与 Git 不同的既定选择 `[已定]`

| 维度 | `sm` 的选择 | 与 Git 的差别 | 理由 |
|---|---|---|---|
| 内容哈希 | SHA-256(`System.Security.Cryptography.SHA256`,见 `Models.cs:156`) | Git 默认 SHA-1,迁移拖了十余年 | 从第一天就用强哈希,不背历史迁移的债 |
| 对象帧 | `"<type> <size>\0" + payload`,zlib 压缩,`.sm/objects/xx/` | 形态相近 | 沿用成熟的内容寻址结构,但哈希换代 |
| 传输协议 | 自研二进制 `sm://`,magic `SMP1`,带版本协商(见 `SmWireProtocol.cs`) | Git 有多代 wire 协议(dumb / smart / v2) | 单一、明确、可版本化的协议 |
| 兼容传输 | file / http(s) / ssh | Git 同 | 复用既有通道,不重新发明 |
| 输出 | `--json` 一级公民 | Git 的 porcelain 输出解析脆弱 | 机器可读是一等需求 |
| 依赖 | 单文件发布,唯一 NuGet 依赖 `System.IO.Hashing` | Git 依赖 C / Perl / shell 一大坨 | 部署简单、可控 |
| THM | .NET 10,`LangVersion 13` | C | 现代运行时,开发效率优先 |

---

## 2. 明确不做 / 暂不做

- `[待定]` **submodule**:当前无此命令。倾向不复制 Git 的 submodule 模型(业界公认失败设计)。
- `[待定]` **worktree / sparse-checkout / LFS / bundle / notes**:当前均无。
- `[待定]` **`clone`**:当前命令集中**没有 `clone`**。需要决定补齐(init + fetch + checkout 的封装)还是明确不做。
- `[待定]` **staging area(index)**:当前有 index(与 Git 同)。是否保留这层,还是像 Mercurial 那样弱化?
- `[待定]` **`checkout` 多义**:Git 因 `checkout` 干太多事而拆出 `switch`/`restore`。`sm` 已有 `restore`,是否也补 `switch` 以拆分语义?

---

## 3. 待拍板的关键契约

### 3.1 可恢复性(reflog)`[已定]`

**决定:实现完整的 reflog,记录所有 HEAD/ref 移动与真实时间戳,恢复能力靠 reflog。**

- 覆盖的操作:`commit`、`checkout`/`switch`、`reset`、`merge`、`rebase`、`cherry-pick`、`revert`、`branch` 增删改、`tag`、`fetch`、`pull`。
- 存储:`logs/HEAD` 与 `logs/refs/...`,行格式为 `<old> <new> <name> <email> <unix> <tz>\t<message>`,时间戳随签名落盘并可解析。
- 命令面:`reflog [show]`、`reflog expire`、`reflog delete` 均为完整实现(无 "not yet implemented")。
- 版本语法:`<ref>@{n}`、`<ref>@{<time>}`(`yesterday` / `2.hours.ago`)、`@{-n}` 对所有 ref 生效。
- 附带修正:`checkout -` 现在通过 reflog 正确返回上一个分支。

> 未选 B/C:不做"对象永不剪枝",也不接受破坏性操作不可逆——reflog 才是 Git 里真正解决这个问题的机制,所以正面把它做全。

### 3.2 GC / 剪枝策略

- **现状**:默认剪枝宽限 `2.weeks.ago`;实测 `gc` 保留不可达对象并提示 grace period。
- **选择**:永久保留 / 定期剪枝 / 仅手动剪枝。
- **待定**:__________

### 3.3 破坏性命令的默认

`reset --hard` / `branch -D` / `checkout` 是否默认允许丢数据?是否需要"先记录再执行"?
- **待定**:__________

### 3.4 历史模型

历史是"只追加",还是允许 `rebase` / `amend` 改写?改写是否算一等操作?
- **待定**:__________

---

## 4. 已发现的实现问题

来自 2026-09-24 用 `sm` 归档 5 个搁置项目的实战测试:

1. ~~`reflog` 不记录 HEAD 移动~~ —— **已修复**(完整 reflog,见 3.1)。
2. ~~`reflog` 时间戳为 `0001-01-01 00:00:00`~~ —— **已修复**(时间戳随签名落盘并解析)。
3. `sm archive` 的帮助中 `--format` 被定义了两次(输出格式 / 压缩格式),语义撞车。**未处理**。

### 其他存量 stub(非 reflog 范围,待排期)

- `bisect run`(BisectCmd.cs)
- `cherry-pick --skip/--quit`(CherryPickCmd.cs)
- `reset --patch`(ResetCmd.cs)
- `revert --skip/--quit`(RevertCmd.cs)

> 宗旨:不接受"最小实现 / TODO / Stub"。以上逐条排期做成完整实现。

---

## 5. 实战验证记录

**2026-09-24** 用 `sm` 把 5 个搁置项目归档进 `_archive`(807 文件,提交 `01372d9`):

- **通过**:`init` / `add` / `commit` / `status` / `log` / `rev-parse` / `ls-files` / `restore`(字节级一致)/ `branch` / `checkout`(分支隔离,字节级还原)/ `reset --hard` / `branch -D` / `archive → zip`(807 条目,含空格路径正确)/ `gc`(尊重宽限)/ `fsck`(无错误)。
- **未验证**:网络半壁 —— `fetch` / `pull` / `push` / `serve`、`sm://` 协议、ssh / http transport。

**2026-09-24** 完整 reflog 落地并回归:

- 手工验证:HEAD 记录 commit/checkout 且时间戳真实;分支各自记账;`HEAD@{n}` / `main@{n}` / `@{time}` 解析;`reflog expire` / `delete` 生效;`checkout -` 正确回到上一分支。
- 回归套件:`tests/run-tests.ps1` **96/96 通过**(原 89 + 新增 7 条 reflog 用例)。

---

## 6. 使用本文件的方式

- 任何人(包括 AI 助手)在给 `sm` 提设计建议前,先读本文件。
- 若建议源自 Git 惯例,必须显式标注:"这是 Git 的先验,未必适用 `sm`"。
- 每条 `[待定]` 拍板后改为 `[已定]` 或 `[拒绝]`,并补一句理由。
