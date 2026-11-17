# 截图占位与替换

[返回首页](../../README.md)

当前 `*-placeholder.svg` 只有文字和简单装饰，**不是实际产品截图**。中英文首页均使用这些存在的 SVG，因此在真实图片提交前不会出现破图。

预留以下真实截图文件名；请将 PNG 放到本目录，再同时修改两个 README 的引用：

| 真实文件名 | 建议内容 | 当前占位 |
| --- | --- | --- |
| `portrait.png` | 手机竖屏应用库或应用工作区 | `portrait-placeholder.svg` |
| `landscape.png` | 手机横屏，包含任务栏和 UU 侧栏关系 | `landscape-placeholder.svg` |
| `ipad.png` | 平板应用库与自身字号比例 | `ipad-placeholder.svg` |
| `dark-mode.png` | 深色主题及控制条 | `dark-mode-placeholder.svg` |

1. 使用真实运行版本截图，不把设计图、模拟数据或离屏预览标成实拍。
2. 检查账号、设备名称、私人窗口标题、路径、通知和内容；裁剪或打码后再提交。
3. 截图尽量保留目标功能的上下文，不通过裁剪隐藏已知遮挡问题。
4. 将 `README.md` 和 `README.en.md` 对应的 `*-placeholder.svg` 改为 PNG 名，更新占位说明和 alt 文本。首页使用相对路径，发行包也包含这些公开图片；增加真实 PNG 后同步更新打包允许列表。
5. 只替换已准备好的图片；未准备的保留占位，不创建空 PNG。

例如竖屏截图就绪后：

```markdown
![手机竖屏应用库](docs/screenshots/portrait.png)
```

The SVG files are labeled placeholders, not screenshots. Add real, redacted PNGs using the reserved names above and update both language versions of the README. Keep the placeholder for any capture that is not ready yet.
