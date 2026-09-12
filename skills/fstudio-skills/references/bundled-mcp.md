# 使用内置 MCP 的完整技能包

Release 的 `Fstudio_skills.zip` 以技能为入口，同时包含对应版本的 MCP 源码：

```text
fstudio-skills/
  SKILL.md
  LICENSE
  agents/
  references/
  scripts/
    check_layout.py
    setup_mcp.ps1
  mcp/
    server.py
    build.ps1
    install-visible-host.ps1
    src/
    ...其余运行依赖源码
```

技能提供制作方法，MCP 提供执行工具。包内附完整源码，但复制技能目录不会自动向 AI 客户端注册 MCP，也不会自动安装 FStudio。仓库的 `skills/fstudio-skills` 是技能维护源；完整下载包由仓库根目录的 `python package-skill.py` 组装，避免在 Git 仓库中复制两份 MCP 源码。

## 接入步骤

1. 将整个 `fstudio-skills` 文件夹放到最终使用位置。若客户端支持 Codex 技能目录，可放在 `~/.codex/skills/fstudio-skills`；其他客户端按其技能机制接入。已连接独立 MCP 的环境可继续复用原连接，无需再建一套。
2. 确认本机已有 Windows、64 位 PowerShell 7、Python 3.11+、.NET Framework 4.x 编译器和合法安装的 FStudio 3。
3. 在 PowerShell 中按实际解释器、工作区和技能目录运行：

   ```powershell
   & 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -File '<技能目录>\scripts\setup_mcp.ps1' -PythonExecutable '<Python解释器完整路径>' -Workspace 'C:\FStudioWork'
   ```

   自定义 FStudio 安装路径时加 `-FStudioRoot 'D:\FStudio'`。工作区尽量使用较短路径，具体工程仍受原生目录长度限制。

4. 脚本在 `mcp/mcp.local.json` 生成 stdio 配置片段，并使原生宿主工作区保持一致。将其中的命令和参数接入 AI 客户端；客户端采用其他配置格式时按字段映射，不覆盖整份现有配置。脚本不会修改全局配置或启动 FStudio。
5. 重新连接该 MCP，在已加载插件的宿主中确认工程和 `fstudio_canvas_snapshot` 等工具可用，再按技能流程制作。技能目录移动后需要重新运行配置脚本以更新绝对路径。

## 更新已有安装

先保存工程并停止相关 MCP/宿主。更新包内源码和技能资源，保留原有 `mcp/projects`、工作区工程及必要配置；不要删除整个已使用的技能文件夹后重新解压。重新运行构建配置脚本时传入原工作区，更新客户端同一个连接，再正常启动。若已有工程位于独立工作区，继续沿用该目录。

技能与 MCP 源码采用仓库标准 MIT 许可证。FStudio 软件及相关厂商资源版权归繁易公司及相应权利人所有；完整技能包不包含厂商 DLL、字体、模板、许可证或用户工程。
