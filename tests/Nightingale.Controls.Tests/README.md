# 播放进度控件回归检查

运行：

```powershell
& D:\tools\dotnet\dotnet.exe run --project tests/Nightingale.Controls.Tests/Nightingale.Controls.Tests.csproj -c Release
```

默认检查在独立 WPF 窗口中加载正式应用的控件和样式，将窗口放在屏幕外，不移动鼠标。它验证实际滑块的尺寸、请求后进度显示、异步 seek 确认、播放轮询、失败恢复、切换媒体、轨道几何，以及方向键释放不会二次 seek。默认检查使用反射触发一次控件内部的请求入口，以便在不依赖当前桌面鼠标状态时验证公开显示结果与事件；它**不验证实际鼠标输入**。

可选鼠标输入检查：

```powershell
& D:\tools\dotnet\dotnet.exe run --project tests/Nightingale.Controls.Tests/Nightingale.Controls.Tests.csproj -c Release -- --pointer
```

`--pointer` 会短暂显示测试窗口并移动系统鼠标，结束后恢复原位置；运行期间不要同时操作鼠标。它根据命中位置向实际可视元素发送 WPF 预览鼠标事件，经过正式事件路由、鼠标捕获和控件处理逻辑，验证轨道点击、宽点击区、拖动、拇指偏移、移出释放和捕获丢失。某些远程桌面／自动化宿主会使 WPF 的全局鼠标坐标缓存与系统坐标不一致，导致这组可选检查报告“Mouse position did not settle”。这种情况是输入环境不满足测试前提，不能据此判定产品逻辑通过或失败，应使用实际界面操作检查。

两组检查都不启动播放器、不加载媒体、不修改用户设置；构建产物位于项目的 `tmp` 目录。
