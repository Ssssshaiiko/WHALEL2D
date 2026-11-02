# Cubism SDK 放这里

渲染器要用 Live2D 官方的 Cubism SDK for Native 编译。插件里不带它，请自己下载（下载时需要同意 Live2D 的许可协议）：

https://www.live2d.com/sdk/download/native/

下载 Windows 用的那个压缩包，解压到这个文件夹（sdk）里，层级不限，例如：

```
sdk\
  CubismSdkForNative-5-r.5\
    Core\
    Framework\
    Samples\ …
```

setup.ps1 会自己找到里面的 `Core\include\Live2DCubismCore.h`。

编译出来的渲染器（renderer\build）里链接了 Live2D 的核心库，只给自己用，不要分享给别人。
