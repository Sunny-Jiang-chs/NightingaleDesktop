# Desktop integration checks

This executable creates the real `MainWindow` and runs the WPF dispatcher and libmpv inside an isolated test process. It loads the application's actual XAML resource definitions but deliberately bypasses `App.OnStartup`, so it never connects to an existing player's single-instance pipe. It does not use UI Automation or modify system file associations.

Run from the repository root:

```powershell
& 'D:\tools\dotnet\dotnet.exe' run --project 'tests\Nightingale.Integration.Tests\Nightingale.Integration.Tests.csproj' --no-launch-profile
```

The repository build settings place build output in `tmp`. Synthetic media, isolated application state, and `result.log` are retained in a timestamped `tmp/integration-tests` directory. Fixtures start muted. The test window normally stays off screen; the fullscreen check briefly opens and restores its own fullscreen window. Pass `-- --skip-fullscreen` after the `dotnet run` arguments to omit that check during other desktop interaction.

Fixture generation uses `D:\tools\ffmpeg\ffmpeg-9.0.1-essentials_build\bin\ffmpeg.exe` by default. Set `NIGHTINGALE_FFMPEG` to an existing `ffmpeg.exe` to override it. If `tmp/qa-media/中文 测试.mp4` exists, it is copied as the long video fixture; otherwise the harness generates that fixture too.

Checks cover automatic SRT selection without duplication, exact external-track mapping, subtitle delay and seek snapping, LRC conversion and visible audio lyrics, embedded cover art, audio-to-video switching, rapid file replacement, history isolation and resume persistence, nonfatal subtitle/property errors, the sleep timer, natural EOF queue progression and completed-history reset, fullscreen controls and Escape, dispatcher exceptions, and mute preservation.

The tests exercise application behavior through its methods and state using reflection. They complement manual native-window interaction and do not claim to validate installer registration, physical keyboard/mouse routing, monitor scaling, codec breadth, or visual appearance.
