# Genius — 生存战争守护灵

住在 Survivalcraft 里的 AI 同伴。召唤它、跟它说话,它自己规划并动手:跟随、探路、挖掘、放置、汇报。

> 罗马神话中,*genius* 是伴随每个人一生的守护灵——与 *numen*(神意)同属一脉。
> 本项目受 Minecraft 模组 [Numen](https://github.com/Dwinovo/minecraft-numen) 启发,
> 是面向 Survivalcraft 的全新净室实现(C# / SurvivalcraftAPI),不包含其任何代码。

## 特性(MVP 目标)

- 🧍 **召唤守护灵**:人形 NPC 实体,跟随玩家
- 💬 **游戏内对话**:聊天窗口直接对话,LLM 驱动
- 🔭 **环境感知**:扫描周围方块与生物并汇报
- ⛏️ **真挖真放**:按 `ComponentMiner` 的真实挖掘耗时/工具选择/耐久消耗执行,不瞬间破坏
  (落地改动经 `SubsystemTerrain`,因引擎的 `ComponentMiner.Dig/Place` 在无 ComponentPlayer 的 NPC 上会崩溃)
- 🔌 **任意 OpenAI 兼容后端**:DeepSeek / Qwen / Kimi / Claude / GPT,密钥仅存本地

完整规划见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 环境

- 游戏:Survivalcraft 2.4(SurvivalcraftAPI 1.44+)
- 构建:.NET 10,Linux/Windows 均可;运行需 Windows 端游戏本体
- 构建时通过 `SurvivalcraftDir` 指向游戏 DLL 目录:

```bash
dotnet build -p:SurvivalcraftDir=/path/to/sc-libs/
dotnet test  -p:SurvivalcraftDir=/path/to/sc-libs/
```

## 许可

代码以木兰宽松许可证第 2 版(MulanPSL-2.0)发布,见 [LICENSE](LICENSE)。
