# 自托管远程同步

Android 在移动网络或异地 Wi-Fi 下可以从自有服务器读取“Windows 电脑”和“ECS 服务器”两台设备的
最近额度与 Token 统计。Windows 关机后可以查看其最近一次上传的快照，手机会显示两项实际采集时间。
快照范围与保留规则见[远程同步合同](../docs/API_CONTRACT.md#自托管远程同步-schemaversion-1)。

## 启动服务

ECS 需要公网可达的 HTTPS 入口、受客户端系统信任的证书，以及与本仓库 [SDK 配置](../global.json) 对应
的 .NET 环境。让反向代理转发 `/v1/remote/` 到本服务的 `127.0.0.1:43901`。ECS 安全组开放 HTTPS
入口，应用端口只在回环地址监听。

以下命令在 ECS 的仓库根目录运行。用运行服务的普通用户创建一次密钥文件，并让该用户独占文件和数据目录。
密钥文件包含两个随机值，`read` 供 Android 使用，`windowsUpload` 供 Windows 使用。

```sh
umask 077
mkdir -p "$HOME/.local/share/codexquota-relay/data"
keyfile="$HOME/.local/share/codexquota-relay/keys.json"
if [ ! -e "$keyfile" ]; then
  python3 -c 'import json, secrets; print(json.dumps({"read": secrets.token_hex(32), "windowsUpload": secrets.token_hex(32)}))' > "$keyfile"
fi
export CODEXQUOTA_RELAY_KEYS="$keyfile"
export CODEXQUOTA_RELAY_DATA="$HOME/.local/share/codexquota-relay/data"
dotnet restore relay/CodexQuotaTray.Relay.csproj --configfile windows/NuGet.Config
dotnet run --project relay/CodexQuotaTray.Relay.csproj --no-restore
```

如果还要显示 ECS 自己的使用情况，用运行 Codex 的同一普通用户启动服务，事先确保该用户能运行 Codex CLI。
按需要指定实际的可执行文件和 Codex home，再设置 `CODEXQUOTA_ECS_COLLECT=true` 并重启服务。关闭该变量
只停止 ECS 本机采集，Windows 上传仍可用。

| 可选环境变量 | 用途 |
| --- | --- |
| `CODEXQUOTA_ECS_COLLECT` | 设为 `true` 启用 ECS 本机采集 |
| `CODEXQUOTA_ECS_CODEX` | Codex 可执行文件路径，默认从 PATH 寻找 `codex` |
| `CODEX_HOME` | 现有 Codex 数据目录，默认沿用 Codex 的用户目录 |

建议通过服务管理器常驻运行并配置退出重启。运行服务的用户及管理员可以访问磁盘上的聚合快照。
反向代理日志应仅记录请求元数据，避免输出 Authorization 请求头或请求体。

## 连接客户端

1. 在 Windows 打开“设置 → 刷新与同步”，在“远程同步”中填写上传入口的 HTTPS 地址与 `windowsUpload`，然后开启远程同步。
2. 在 Android 打开“设置 → 远程同步 → 服务器连接”，填读取入口的 HTTPS 地址与 `read`。保存后打开对应的设备详情。
3. 要让 Android 主界面使用远程快照，在“设置 → 数据”中给额度或统计选择“远程同步”，再选择设备。

Windows 与 ECS 每 5 分钟尝试采集一次，读取失败保留最后有效快照。只有 ECS 本机 Token 汇总使用本机
会话文件。Windows 上传使用 WinUI 当前所选的 Token 来源。

## 公网只读入口

Windows 可以经 Tailscale Serve 上传，Android 可以经独立的公网 HTTPS 入口读取。单独运行一个 relay
进程，设置 `CODEXQUOTA_RELAY_PHONE_MODE=forward`，并将公网代理指向它的回环地址
`127.0.0.1:43902`。该进程只转发两台设备的 GET 请求，由主服务校验手机读取密钥，且无需配置密钥
文件或数据目录。Windows 继续使用原来的上传地址。

使用 [Tailscale Funnel](https://tailscale.com/docs/features/tailscale-funnel) 时，只授权运行 relay
的节点，并为公网读取入口选用与私有 Serve 不同的 HTTPS 端口。Android 填写完整的 HTTPS 地址，
包含端口。联通性测试可将网关模式设为 `probe`，用由 64 个零组成的测试密钥读取匿名快照。
测试结束后应切换到 `forward` 或关闭入口。
