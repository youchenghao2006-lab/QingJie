# 轻截 QingJie

轻截是一款 Windows 截图工具。按 `F2` 框选屏幕，松开鼠标后可以调整范围、画箭头、打马赛克或添加文字。完成后，可复制、保存，也可以直接把图片固定在桌面上。

- **滚动长截图**。框选网页、文档或聊天记录后，在原页面向下滚动。右下角的小预览显示拼接进度，按 `Ctrl + Enter` 完成。
- **多屏截图与贴屏**。可单独截取鼠标所在的显示器，将贴图拖到另一块屏幕，或用滚轮缩放查看细节。
- **自由画布**。从截图队列拖入多张图片，在同一张画布上用箭头、画笔和文字说明它们的关系，然后导出合成图。
- **本地历史**。截图和画布导出分别存入程序目录下的 `历史记录/截图` 和 `历史记录/画布`，都可以重新导入编辑。

更多操作和快捷键见 [使用说明](./使用说明.txt)。

## 下载

在 [GitHub Releases](https://github.com/youchenghao2006-lab/QingJie/releases) 下载 Windows 便携版，解压到可写入的文件夹，双击 `QingJie.exe` 启动。

## 构建与运行

在装有 .NET Framework 4.8 的 Windows 10 或 11 上，打开 PowerShell，进入项目根目录并运行：

```powershell
.\build.ps1
.\QingJie.exe
```

构建脚本使用 Windows 的 .NET Framework C# 编译器，生成的 `QingJie.exe` 位于项目根目录。复制程序到其他位置时，请将同目录下的 `QingJie.exe.config` 一起带上。

## 测试

核心回归涵盖截图编辑、历史记录、画布和队列。其余四个脚本分别验证快捷截图、框选遮罩、长图拼接和滚动采集会话。

```powershell
.\run-tests.ps1
.\run-quick-capture-tests.ps1
.\run-region-overlay-tests.ps1
.\run-scroll-tests.ps1
.\run-scroll-session-tests.ps1
```

应用源码位于 `Source/`，测试源码位于 `Tests/`。已有的性能测量、合成样例和复现方法见 [性能验证](./性能验证.txt)。

## 许可证

轻截使用 [MIT 许可证](./LICENSE)。
