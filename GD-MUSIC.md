# GD音乐台版 Pulsar

在 SOURCE 选择 **GD音乐台**，选择音乐平台和音质，输入歌名或歌手并搜索。
点击 **Play** 播放，或 **Queue** 加入队列。暂停、进度、音量、上一首/下一首沿用原版播放器。
启用 **On Air** 后沿用 Pulsar 原有的音频准备和同步服务。

来源：GD音乐台(music.gdstudio.xyz)。API 文档：https://music-api.gdstudio.xyz/api.php
目前服务方列出的稳定平台为 netease、joox、bilibili；其余平台可能未开放或不稳定。
搜索到歌曲不保证可播放；没有音源、网络失败和限流会显示错误。
每个实例限制每 5 分钟最多 50 次 API 请求。默认 128 kbps，最高支持接口返回的无损音源。

音频缓存位于 Dalamud 的 Pulsar 配置目录 `gd-music-cache`，单首上限 256 MB。
内置播放器和转码器为原版 Pulsar 的 hosts。无需填写网易云 App ID 或私钥，无需 Node.js、ncm-cli 或 mpv。
GD 服务方声明资源仅限本人学习参考，禁止下载、传播或商用；API 的可用性和资源授权范围由服务方负责说明。
在使用音频同步前，应确认相应音源许可允许该用途。

加载 `loadable/Pulsar/Pulsar.dll`，保留整个文件夹。未制作压缩包。
