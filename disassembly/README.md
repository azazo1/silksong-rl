# 反编译产物

这里是用 dnSpyEx 的 `dnSpy.Console.exe` 反编译出的游戏 C# 源码, 只用于阅读和跳转, 不能重新编译.

- 目录结构: 全局命名空间的类在顶层各程序集目录下, 其余按命名空间分层.
- 覆盖范围, 限制与使用建议见 `docs/silksong-modding-guide.md` 第 7 节.
- 重新生成: `pwsh -File disassembly/decompile.ps1` (约一到两分钟, 期间无输出).
- 本目录不进版本库.
