# 鸿蒙应用维护工具

Windows 内部运维工具原型。它只面向获得明确授权、且提供正式维护接口的目标应用。

## 工作方式

工具通过本机 `hdc.exe` 在已授权的测试/企业设备上,以**目标应用自身身份**启动设备端 `bftpd`,再让设备端短生命周期客户端完成**该应用自己沙箱目录**的维护:

1. `hdc list targets` 发现设备;
2. `hdc shell aa process -b <bundle> -a <ability> -p "/system/bin/bftpd -D -p 8100" -S` 以应用 UID 启动 FTP 服务(仅对 debug 签名应用可用);
3. 列表/下载/上传/建目录/删除全部走设备端 `ftpget`/`ftpput` 短连接命令,文件本体经 `hdc file send/recv` 桥接;
4. 不依赖端口转发与常驻控制连接;全程路径白名单校验并写本地审计日志;
5. 相册/视频下载为独立只读流程,经 `mediatool` 访问媒体库。

## 当前功能

- 自动探测/引导安装本机 `hdc.exe`(配置路径 → SDK/DevEco Studio → 手动指定),并把路径写回配置;
- 通过 `hdc.exe` 检测已授权的 HarmonyOS 设备;
- 连接设备后自动启动设备端 `bftpd` 并建立受控 FTP 通道;
- 浏览目标应用受控沙箱目录(仅限配置的白名单根);
- 下载 / 上传 / 递归传输 / 删除,全程写本地审计日志;
- 支持将 `.hap` / `.app` 安装包拖入主窗口投放区,经 `hdc install` 官方通道安装/覆盖更新到当前设备(与沙箱 FTP 维护相互独立,安装前二次确认并审计);
- 提供拒绝敏感路径的 `PathPolicyService`;
- 可选的相册/视频只读下载窗口(`mediatool` 通道)。

## HDC 工具链获取

- 程序启动时依次探测:配置的 `HdcPath` → 工具目录 `toolchains` → `%LOCALAPPDATA%\OpenHarmony\Sdk` / `Huawei\Sdk` → DevEco Studio 自带 SDK → `PATH`;
- 探测不到时弹出引导窗口:可**自动下载**(需在 `HdcInstall.DownloadUrl` 配置含 hdc.exe 的压缩包直链)、**选择压缩包安装**、**手动指定 hdc.exe**,或打开官方下载页;
- 也可点击主界面左侧「HDC 设置」随时更换路径;
- USB 连接设备还需安装驱动;支持网络调试的设备可用 `hdc tconn` 免驱动。

## 安全边界

- 仅连接已授权的测试或企业设备,所有操作写入审计日志;
- 不实现绕过鸿蒙应用沙箱的访问方式;`bftpd` 以目标应用自身身份运行,仅能访问其自有沙箱,不跨应用读取数据;
- 文件浏览仅允许 `ManagedApplication.AllowedRoots` 中的沙箱目录;
- `BlockedPathFragments` 命中即拒绝(凭据、令牌、密钥、数据库、偏好设置等);
- 上传覆盖、删除属于破坏性操作,必须由 UI 二次确认。

在 [appsettings.json](appsettings.json) 中填写经授权的应用信息与允许访问的沙箱根目录;不要将 `preferences` 等敏感目录加入白名单。

## 构建前提

需要安装 .NET 8 SDK 的 Windows 桌面开发工作负载(已验证 8.0.424 可编译)。

```powershell
dotnet build .\HarmonyFileManager.csproj
```
