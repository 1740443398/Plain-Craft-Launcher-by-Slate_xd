Imports System.IO
Imports System.Diagnostics
Imports System.Text
Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq

''' <summary>
''' 一键开服器：列出本地服务端版本、下载官方服务端、按版本隔离启动。
''' <para>
''' <b>为什么只下载服务端</b>：Mojang 的版本清单里，客户端与服务端是两个独立条目
''' （<c>downloads.client</c> 与 <c>downloads.server</c>）。开服器只取 server，
''' 界面上也不出现任何「客户端下载」入口——避免玩家误装成客户端。
''' </para>
''' <para>
''' <b>版本隔离</b>：每个服务端版本放在独立的 <c>PCL\Servers\&lt;版本&gt;\</c> 下，
''' 各自有 server.properties / world / 模组，互不干扰。
''' </para>
''' </summary>
Partial Public Module ModServer
    ''' <summary>服务器根目录。</summary>
    Public ReadOnly Property ServerDir As String
        Get
            Return Paths.Base & "PCL\Servers\"
        End Get
    End Property

    ''' <summary>一个服务端版本（对应一个隔离目录）。</summary>
    Public Class ServerVersion
        ''' <summary>版本名，也就是目录名，如 1.20.1。</summary>
        Public Property Name As String = ""
        ''' <summary>是否已下载 server.jar。</summary>
        Public Property Installed As Boolean = False
        ''' <summary>server.jar 的字节数（未安装时 0）。</summary>
        Public Property JarSize As Long = 0
        ''' <summary>已同意 EULA。</summary>
        Public Property EulaAccepted As Boolean = False
        ''' <summary>端口。</summary>
        Public Property Port As Integer = 25565
        ''' <summary>分配内存（MB）。</summary>
        Public Property MemoryMB As Integer = 2048
        ''' <summary>正版验证。</summary>
        Public Property OnlineMode As Boolean = True
        ''' <summary>motd。</summary>
        Public Property Motd As String = ""
        ''' <summary>是否启用（用于控制列表里的启动按钮）。</summary>
        Public Property Enabled As Boolean = True

        '—— 以下是 server.properties 的可视化字段 ——
        '默认值对齐 Minecraft 官方默认，改之前先看官方文档：
        'https://minecraft.wiki/w/Server.properties

        ''' <summary>最大玩家数。</summary>
        Public Property MaxPlayers As Integer = 20
        ''' <summary>视距（区块）。</summary>
        Public Property ViewDistance As Integer = 10
        ''' <summary>模拟距离（区块）。</summary>
        Public Property SimulationDistance As Integer = 10
        ''' <summary>难度：peaceful / easy / normal / hard。</summary>
        Public Property Difficulty As String = "easy"
        ''' <summary>默认游戏模式：survival / creative / adventure / spectator。</summary>
        Public Property GameMode As String = "survival"
        ''' <summary>玩家互相伤害。</summary>
        Public Property Pvp As Boolean = True
        ''' <summary>极限模式。</summary>
        Public Property Hardcore As Boolean = False
        ''' <summary>允许飞行（防作弊用，开了会让飞行外挂也能用）。</summary>
        Public Property AllowFlight As Boolean = False
        ''' <summary>允许下界。</summary>
        Public Property AllowNether As Boolean = True
        ''' <summary>出生点保护半径（格）。</summary>
        Public Property SpawnProtection As Integer = 16
        ''' <summary>世界文件夹名。</summary>
        Public Property LevelName As String = "world"
        ''' <summary>世界种子。留空表示随机。</summary>
        Public Property LevelSeed As String = ""
        ''' <summary>世界类型：minecraft:normal / flat / large_biomes / amplified。</summary>
        Public Property LevelType As String = "minecraft:normal"
        ''' <summary>启用白名单。</summary>
        Public Property WhiteList As Boolean = False
        ''' <summary>白名单外的玩家一律踢出。</summary>
        Public Property EnforceWhitelist As Boolean = False
        ''' <summary>生成怪物。</summary>
        Public Property SpawnMonsters As Boolean = True
        ''' <summary>生成动物。</summary>
        Public Property SpawnAnimals As Boolean = True

        '—— 扩展项（RCON / Query / 高级）——
        '这些键在旧版本服务端上可能不存在，WriteProperties 会把它们补在文件末尾；
        '服务端自己不认识时会忽略，不会导致启动失败。

        ''' <summary>允许命令方块。</summary>
        Public Property EnableCommandBlock As Boolean = False
        ''' <summary>OP 权限等级 1-4。4 等于全权限。</summary>
        Public Property OpPermissionLevel As Integer = 4
        ''' <summary>函数（数据包）权限等级 1-4。</summary>
        Public Property FunctionPermissionLevel As Integer = 2
        ''' <summary>启用 Query 协议（GameSpy4，供第三方查询在线列表）。</summary>
        Public Property EnableQuery As Boolean = False
        ''' <summary>Query 端口。默认与服务端端口相同。</summary>
        Public Property QueryPort As Integer = 25565
        ''' <summary>启用 RCON 远程控制。</summary>
        Public Property EnableRcon As Boolean = False
        ''' <summary>RCON 端口。</summary>
        Public Property RconPort As Integer = 25575
        ''' <summary>RCON 密码。开着 RCON 却没密码时服务端会拒绝启动，UI 需要校验。</summary>
        Public Property RconPassword As String = ""
        ''' <summary>在服务器列表里显示状态（在线人数等）。</summary>
        Public Property EnableStatus As Boolean = True
        ''' <summary>挂机多少分钟后踢出，0 表示不踢。</summary>
        Public Property PlayerIdleTimeout As Integer = 0
        ''' <summary>每次进服都强制切成默认游戏模式。</summary>
        Public Property ForceGameMode As Boolean = False
        ''' <summary>世界边界半径上限（格）。</summary>
        Public Property MaxWorldSize As Integer = 29999984
        ''' <summary>网络压缩阈值（字节）。-1 表示不压缩，0 表示全压缩。</summary>
        Public Property NetworkCompressionThreshold As Integer = 256
        ''' <summary>单玩家每秒数据包上限，0 表示不限。</summary>
        Public Property RateLimit As Integer = 0
        ''' <summary>实体广播范围百分比（10-1000）。</summary>
        Public Property EntityBroadcastRangePercentage As Integer = 100
        ''' <summary>拒绝通过代理连接的玩家（防 VPN 绕过封禁）。</summary>
        Public Property PreventProxyConnections As Boolean = False
        ''' <summary>Linux 上使用 epoll 原生传输。Windows 上无效果。</summary>
        Public Property UseNativeTransport As Boolean = True
        ''' <summary>区块写入同步落盘。断电更安全，但更慢。</summary>
        Public Property SyncChunkWrites As Boolean = True

        Public ReadOnly Property Dir As String
            Get
                Return System.IO.Path.Combine(ServerDir, Name)
            End Get
        End Property

        Public ReadOnly Property JarPath As String
            Get
                Return System.IO.Path.Combine(Dir, "server.jar")
            End Get
        End Property

        Public ReadOnly Property EulaPath As String
            Get
                Return System.IO.Path.Combine(Dir, "eula.txt")
            End Get
        End Property

        Public ReadOnly Property ConfigPath As String
            Get
                Return System.IO.Path.Combine(Dir, "pcl-server.json")
            End Get
        End Property

        Public ReadOnly Property ReadyText As String
            Get
                If Not Installed Then Return "未下载服务端"
                If Not EulaAccepted Then Return "已下载，需同意 EULA"
                Return "可以启动"
            End Get
        End Property
    End Class

    '==== 本地服务端版本 ====

    Public ReadOnly LocalVersions As New List(Of ServerVersion)

    ''' <summary>扫描本地的服务端版本目录。</summary>
    Public Sub ScanLocal()
        LocalVersions.Clear()
        Try
            DirectoryUtils.Create(ServerDir)
            For Each SubDir In Directory.GetDirectories(ServerDir)
                Try
                    Dim Name = PathUtils.GetLastPart(SubDir)
                    Dim Jar = System.IO.Path.Combine(SubDir, "server.jar")
                    Dim Cfg = System.IO.Path.Combine(SubDir, "pcl-server.json")
                    Dim V As New ServerVersion With {.Name = Name}

                    If FileUtils.Exists(Cfg) Then
                        Try
                            '整个对象反序列化，而不是逐字段抄——
                            '早先只抄了 Port/MemoryMB/OnlineMode/Motd/Enabled 五项，
                            '新增的配置项每次重启都会被默认值覆盖掉。
                            Dim Loaded = Newtonsoft.Json.JsonConvert.DeserializeObject(Of ServerVersion)(FileUtils.ReadAsString(Cfg))
                            If Loaded IsNot Nothing Then
                                Loaded.Name = Name '目录名是权威，配置文件里的 Name 不可信
                                V = Loaded
                            End If
                        Catch ex As Exception
                            Logger.Warn(ex, $"读取服务端配置失败：{Name}")
                        End Try
                    End If

                    If FileUtils.Exists(Jar) Then
                        V.Installed = True
                        V.JarSize = New FileInfo(Jar).Length
                    End If
                    V.EulaAccepted = FileUtils.Exists(System.IO.Path.Combine(SubDir, "eula.txt")) AndAlso
                                     FileUtils.ReadAsString(System.IO.Path.Combine(SubDir, "eula.txt")).Contains("eula=true")

                    LocalVersions.Add(V)
                Catch ex As Exception
                    Logger.Warn(ex, $"扫描服务端目录失败：{SubDir}")
                End Try
            Next
            Logger.Info($"服务端扫描完成：{LocalVersions.Count} 个版本")
        Catch ex As Exception
            Logger.Error(ex, "扫描服务端失败")
        End Try
    End Sub

    Public Sub SaveConfig(V As ServerVersion)
        Try
            If V Is Nothing OrElse String.IsNullOrWhiteSpace(V.Name) Then Return
            DirectoryUtils.Create(V.Dir)
            Dim Json = Newtonsoft.Json.JsonConvert.SerializeObject(V, Newtonsoft.Json.Formatting.Indented)
            FileUtils.Write(V.ConfigPath, Json, Encoding.UTF8)

            '★ 关键：pcl-server.json 只是我们自己的镜像，Minecraft 服务端实际读的是 server.properties。
            '原来只写 JSON 不写 properties，玩家在「配置」里改 MOTD / 最大人数 / 视距 全部不生效，
            '而且下次启动时 WriteProperties 又会拿 JSON 里的旧值覆盖回去 —— 看起来像是「设置没保存」。
            '这里补上同步。若 properties 还不存在（尚未首次启动过），写一份完整默认值，
            '这样配置页改的东西在第一次启动时就是对的。
            WriteProperties(V)
        Catch ex As Exception
            Logger.Error(ex, "保存服务端配置失败")
        End Try
    End Sub

    ''' <summary>删除一个服务端（连同世界存档）。</summary>
    Public Sub DeleteVersion(V As ServerVersion)
        Try
            If V Is Nothing OrElse Not DirectoryUtils.Exists(V.Dir) Then Return
            Directory.Delete(V.Dir, True)
            LocalVersions.Remove(V)
            Logger.Info($"已删除服务端：{V.Name}")
        Catch ex As Exception
            Logger.Error(ex, "删除服务端失败")
        End Try
    End Sub

    '==== 可下载版本 ====

    ''' <summary>可下载的服务端版本（来自 Mojang 版本清单）。</summary>
    Public Class RemoteVersion
        Public Property Id As String = ""
        Public Property Type As String = "release"
        Public Property Url As String = ""
        Public Property Size As Long = 0
        Public Property Sha1 As String = ""
        Public Property ReleaseTime As String = ""
    End Class

    Public ReadOnly RemoteVersions As New List(Of RemoteVersion)

    ''' <summary>
    ''' 拉取可下载的服务端版本列表。
    ''' <para>
    ''' 只保留 <c>downloads.server</c> 存在的版本——早期版本（如 1.2.5 之前）
    ''' 没有独立服务端，强行列出会让玩家下载失败。
    ''' </para>
    ''' </summary>
    Public Function FetchRemote(Optional IncludeSnapshot As Boolean = False) As Boolean
        Try
            RemoteVersions.Clear()
            Dim Manifest As String = NetGetString("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json")
            If String.IsNullOrWhiteSpace(Manifest) Then Return False
            Dim Root As JObject = Newtonsoft.Json.JsonConvert.DeserializeObject(Of JObject)(Manifest)
            If Root Is Nothing Then Return False

            Dim All As JArray = Root("versions")
            If All Is Nothing Then Return False

            Dim Count As Integer = 0
            For Each Item In All
                Dim T = Item("type")?.ToString()
                If T <> "release" AndAlso Not (IncludeSnapshot AndAlso T = "snapshot") Then Continue For
                '最多取 60 个，够用且不至于每次都拉几百次
                If Count >= 60 Then Exit For

                Dim Url = Item("url")?.ToString()
                If String.IsNullOrWhiteSpace(Url) Then Continue For
                Try
                    Dim Detail As String = NetGetString(Url)
                    If String.IsNullOrWhiteSpace(Detail) Then Continue For
                    Dim DJ As JObject = Newtonsoft.Json.JsonConvert.DeserializeObject(Of JObject)(Detail)
                    Dim Srv As JObject = DJ("downloads")?.Item("server")
                    If Srv Is Nothing Then Continue For

                    Dim Size = If(Srv("size")?.ToObject(Of Long)(), 0L)
                    If Size <= 0 Then Continue For

                    RemoteVersions.Add(New RemoteVersion With {
                        .Id = Item("id")?.ToString(),
                        .Type = T,
                        .Url = Srv("url")?.ToString(),
                        .Size = Size,
                        .Sha1 = Srv("sha1")?.ToString(),
                        .ReleaseTime = If(Item("releaseTime")?.ToString(), "").Split("T"c)(0)
                    })
                    Count += 1
                Catch ex As Exception
                    Logger.Warn(ex, $"读取版本详情失败：{Item("id")}")
                End Try
            Next
            Logger.Info($"服务端版本列表：拉到 {RemoteVersions.Count} 个可下载版本")
            Return RemoteVersions.Count > 0
        Catch ex As Exception
            Logger.Error(ex, "拉取服务端版本列表失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 下载服务端 jar 到指定版本目录。
    ''' </summary>
    Public Function DownloadServer(JarUrl As String, VersionName As String, ExpectedSize As Long,
                                   Optional Loader As LoaderBase = Nothing) As Boolean
        Try
            If String.IsNullOrWhiteSpace(VersionName) Then Return False
            Dim Dir = System.IO.Path.Combine(ServerDir, VersionName)
            DirectoryUtils.Create(Dir)
            Dim Target = System.IO.Path.Combine(Dir, "server.jar")
            Dim Temp = Target & ".download"

            Logger.Info($"下载服务端 {VersionName}：{JarUrl}")
            Dim Data As Byte() = NetGetBytes(JarUrl, Loader)
            If Data Is Nothing OrElse Data.Length < 1024 * 1024 Then
                Hint("服务端下载失败或文件过小", HintType.Red)
                Return False
            End If

            '校验：jar 是 zip，头两个字节应为 PK
            If Data.Length < 2 OrElse Data(0) <> &H50 OrElse Data(1) <> &H4B Then
                Hint("下载到的不是有效的服务端 jar", HintType.Red)
                Return False
            End If

            IO.File.WriteAllBytes(Temp, Data)
            If FileUtils.Exists(Target) Then FileUtils.Delete(Target, True)
            IO.File.Copy(Temp, Target, True)
            FileUtils.Delete(Temp, True)
            Logger.Info($"服务端已保存：{Target}（{StringUtils.FormatByteSize(Data.Length)}）")

            '写一份配置
            Dim V As New ServerVersion With {.Name = VersionName, .Installed = True, .JarSize = Data.Length}
            SaveConfig(V)
            Return True
        Catch ex As Exception
            Logger.Error(ex, "下载服务端失败")
            Hint($"下载失败：{ex.Message}", HintType.Red)
            Return False
        End Try
    End Function

    '==== EULA 与启动 ====

    ''' <summary>写入 eula.txt。必须由用户明确同意后调用。</summary>
    Public Function AcceptEula(V As ServerVersion) As Boolean
        Try
            If V Is Nothing Then Return False
            DirectoryUtils.Create(V.Dir)
            FileUtils.Write(V.EulaPath,
                "# 由 PCL 生成。同意 Minecraft EULA 后才能开服。" & vbCrLf &
                "# https://aka.ms/MinecraftEULA" & vbCrLf &
                "eula=true" & vbCrLf, New UTF8Encoding(False))
            V.EulaAccepted = True
            SaveConfig(V)
            Logger.Info($"已同意 EULA：{V.Name}")
            Return True
        Catch ex As Exception
            Logger.Error(ex, "写入 EULA 失败")
            Return False
        End Try
    End Function

    ''' <summary>写入 server.properties（只改我们关心的几项）。</summary>
    Public Sub WriteProperties(V As ServerVersion)
        Try
            If V Is Nothing Then Return
            Dim P = System.IO.Path.Combine(V.Dir, "server.properties")
            Dim Lines As New List(Of String)
            '支持的可视化键。其余键原样保留，不破坏服主手写的配置。
            Dim Handled As New HashSet(Of String) From {
                "server-port", "online-mode", "motd",
                "max-players", "view-distance", "simulation-distance",
                "difficulty", "gamemode", "pvp", "hardcore",
                "allow-flight", "allow-nether", "spawn-protection",
                "level-name", "level-seed", "level-type",
                "white-list", "enforce-whitelist", "spawn-monsters", "spawn-animals",
                "enable-command-block", "op-permission-level", "function-permission-level",
                "enable-query", "query.port",
                "enable-rcon", "rcon.port", "rcon.password",
                "enable-status", "player-idle-timeout", "force-gamemode",
                "max-world-size", "network-compression-threshold", "rate-limit",
                "entity-broadcast-range-percentage", "prevent-proxy-connections",
                "use-native-transport", "sync-chunk-writes"
            }

            If FileUtils.Exists(P) Then
                For Each Line In FileUtils.ReadAsString(P).Split({vbCrLf, vbLf}, StringSplitOptions.None)
                    Dim T = Line.Trim()
                    If T = "" OrElse T.StartsWith("#") Then
                        Lines.Add(Line) : Continue For
                    End If
                    Dim Eq = T.IndexOf("="c)
                    If Eq > 0 Then
                        Dim K = T.Substring(0, Eq).Trim()
                        Dim NewVal As String = PropertyValue(V, K)
                        If NewVal IsNot Nothing Then
                            Lines.Add($"{K}={NewVal}") : Handled.Remove(K) : Continue For
                        End If
                    End If
                    Lines.Add(Line)
                Next
            End If

            '文件里原本没有的键，补在末尾
            For Each K In Handled
                Dim NewVal As String = PropertyValue(V, K)
                If NewVal IsNot Nothing Then Lines.Add($"{K}={NewVal}")
            Next

            FileUtils.Write(P, String.Join(vbCrLf, Lines) & vbCrLf, Encoding.UTF8)
        Catch ex As Exception
            Logger.Error(ex, "写入 server.properties 失败")
        End Try
    End Sub

    ''' <summary>
    ''' 取某个 server.properties 键的值。返回 Nothing 表示这个键不由我们管理。
    ''' </summary>
    Friend Function PropertyValue(V As ServerVersion, Key As String) As String
        If V Is Nothing Then Return Nothing
        Select Case Key
            Case "server-port" : Return V.Port.ToString()
            Case "online-mode" : Return BoolText(V.OnlineMode)
            Case "motd" : Return If(String.IsNullOrWhiteSpace(V.Motd), V.Name, V.Motd)
            Case "max-players" : Return V.MaxPlayers.ToString()
            Case "view-distance" : Return V.ViewDistance.ToString()
            Case "simulation-distance" : Return V.SimulationDistance.ToString()
            Case "difficulty" : Return V.Difficulty
            Case "gamemode" : Return V.GameMode
            Case "pvp" : Return BoolText(V.Pvp)
            Case "hardcore" : Return BoolText(V.Hardcore)
            Case "allow-flight" : Return BoolText(V.AllowFlight)
            Case "allow-nether" : Return BoolText(V.AllowNether)
            Case "spawn-protection" : Return V.SpawnProtection.ToString()
            Case "level-name" : Return If(String.IsNullOrWhiteSpace(V.LevelName), "world", V.LevelName)
            Case "level-seed" : Return If(V.LevelSeed, "")
            Case "level-type" : Return If(String.IsNullOrWhiteSpace(V.LevelType), "minecraft:normal", V.LevelType)
            Case "white-list" : Return BoolText(V.WhiteList)
            Case "enforce-whitelist" : Return BoolText(V.EnforceWhitelist)
            Case "spawn-monsters" : Return BoolText(V.SpawnMonsters)
            Case "spawn-animals" : Return BoolText(V.SpawnAnimals)

            '—— 扩展项 ——
            Case "enable-command-block" : Return BoolText(V.EnableCommandBlock)
            Case "op-permission-level" : Return ClampInt(V.OpPermissionLevel, 1, 4, 4).ToString()
            Case "function-permission-level" : Return ClampInt(V.FunctionPermissionLevel, 1, 4, 2).ToString()
            Case "enable-query" : Return BoolText(V.EnableQuery)
            Case "query.port" : Return ClampInt(V.QueryPort, 1, 65535, 25565).ToString()
            Case "enable-rcon" : Return BoolText(V.EnableRcon)
            Case "rcon.port" : Return ClampInt(V.RconPort, 1, 65535, 25575).ToString()
            Case "rcon.password" : Return If(V.RconPassword, "")
            Case "enable-status" : Return BoolText(V.EnableStatus)
            Case "player-idle-timeout" : Return Math.Max(0, V.PlayerIdleTimeout).ToString()
            Case "force-gamemode" : Return BoolText(V.ForceGameMode)
            Case "max-world-size" : Return ClampInt(V.MaxWorldSize, 1, 29999984, 29999984).ToString()
            Case "network-compression-threshold" : Return ClampInt(V.NetworkCompressionThreshold, -1, 65536, 256).ToString()
            Case "rate-limit" : Return Math.Max(0, V.RateLimit).ToString()
            Case "entity-broadcast-range-percentage" : Return ClampInt(V.EntityBroadcastRangePercentage, 10, 1000, 100).ToString()
            Case "prevent-proxy-connections" : Return BoolText(V.PreventProxyConnections)
            Case "use-native-transport" : Return BoolText(V.UseNativeTransport)
            Case "sync-chunk-writes" : Return BoolText(V.SyncChunkWrites)
            Case Else : Return Nothing
        End Select
    End Function

    ''' <summary>server.properties 里的布尔值是全小写的 true / false。</summary>
    Private Function BoolText(B As Boolean) As String
        Return If(B, "true", "false")
    End Function

    ''' <summary>把数值夹到合法区间；非法值退回默认，避免写出让服务端起不来的配置。</summary>
    Private Function ClampInt(Value As Integer, Min As Integer, Max As Integer, Fallback As Integer) As Integer
        If Value < Min OrElse Value > Max Then Return Fallback
        Return Value
    End Function

    ''' <summary>
    ''' 读回 server.properties 的所有键值（忽略注释与空行）。
    ''' 用于备份前快照、配置对比、以及 UI 回显服主手改过的值。
    ''' </summary>
    Public Function ReadProperties(V As ServerVersion) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Try
            If V Is Nothing Then Return Result
            Dim P = System.IO.Path.Combine(V.Dir, "server.properties")
            If Not FileUtils.Exists(P) Then Return Result
            For Each Line In FileUtils.ReadAsString(P).Split({vbCrLf, vbLf}, StringSplitOptions.None)
                Dim T = Line.Trim()
                If T.Length = 0 OrElse T.StartsWith("#") Then Continue For
                Dim Eq = T.IndexOf("="c)
                If Eq <= 0 Then Continue For
                Result(T.Substring(0, Eq).Trim()) = T.Substring(Eq + 1).Trim()
            Next
        Catch ex As Exception
            Logger.Warn(ex, "读取 server.properties 失败")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 配置合法性自检。返回空串表示没问题，否则返回给玩家看的错误说明。
    ''' <para>
    ''' 这些是服务端会直接拒绝启动的硬性冲突，提前拦下来比让玩家对着
    ''' 一闪而过的控制台窗口猜原因好得多。
    ''' </para>
    ''' </summary>
    Public Function ValidateConfig(V As ServerVersion) As String
        If V Is Nothing Then Return "没有指定服务端版本"
        If V.Port < 1 OrElse V.Port > 65535 Then Return "端口必须在 1-65535 之间"
        If V.RconPort < 1 OrElse V.RconPort > 65535 Then Return "RCON 端口必须在 1-65535 之间"
        If V.QueryPort < 1 OrElse V.QueryPort > 65535 Then Return "Query 端口必须在 1-65535 之间"
        If V.EnableRcon AndAlso String.IsNullOrWhiteSpace(V.RconPassword) Then
            Return "开启 RCON 必须设置密码，否则服务端会拒绝启动"
        End If
        If V.EnableRcon AndAlso V.RconPort = V.Port Then
            Return "RCON 端口不能和服务端端口相同"
        End If
        If V.EnableQuery AndAlso V.QueryPort = V.Port Then
            Return "Query 端口不能和服务端端口相同"
        End If
        If V.EnableRcon AndAlso V.EnableQuery AndAlso V.RconPort = V.QueryPort Then
            Return "RCON 端口不能和 Query 端口相同"
        End If
        If V.MemoryMB < 512 Then Return "内存至少 512 MB"
        Return ""
    End Function

    ''' <summary>找可用的 Java（1.17+ 服务器需要 Java 17）。</summary>
    ''' <summary>
    ''' 找可用的 Java。
    ''' <para>
    ''' 必须按服务端要求的版本挑：Minecraft 26.1+ 要求 Java 25，
    ''' 用 Java 21 启动只会打印一句 requires Java 25 然后立刻退出，
    ''' 玩家体感就是「点了启动没反应」。
    ''' </para>
    ''' </summary>
    ''' <param name="MinJavaMajor">最低 Java 主版本；0 表示不限制。</param>
    Public Function FindJava(Optional MinJavaMajor As Integer = 0) As String
        Dim Candidates As New List(Of String)
        Try
            Dim Raw As String = Settings.Get(Of String)("LaunchArgumentJavaAll")
            If Not String.IsNullOrWhiteSpace(Raw) Then
                Dim Arr As JArray = JArray.Parse(Raw)
                For Each Item In Arr
                    Dim P As String = If(Item("Path")?.ToString(), "")
                    If Not String.IsNullOrWhiteSpace(P) AndAlso FileUtils.Exists(P) Then Candidates.Add(P)
                Next
            End If
            Dim Sel As String = Settings.Get(Of String)("LaunchArgumentJavaSelect")
            If Not String.IsNullOrWhiteSpace(Sel) AndAlso Sel.StartsWith("{", StringComparison.Ordinal) Then
                Dim P As String = JObject.Parse(Sel)("Path")?.ToString()
                If Not String.IsNullOrWhiteSpace(P) AndAlso FileUtils.Exists(P) Then Candidates.Add(P)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "从设置读取 Java 列表失败")
        End Try
        'Mojang 随游戏下载的运行时也要算进来，它们常是唯一满足新版要求的
        Try
            Dim Rt = System.IO.Path.Combine(Paths.AppData, ".minecraft", "runtime")
            If DirectoryUtils.Exists(Rt) Then
                For Each D In System.IO.Directory.GetDirectories(Rt)
                    Dim P = System.IO.Path.Combine(D, "bin", "java.exe")
                    If FileUtils.Exists(P) Then Candidates.Add(P)
                Next
            End If
        Catch
        End Try
        Try
            Dim Env = Environment.GetEnvironmentVariable("JAVA_HOME")
            If Not String.IsNullOrWhiteSpace(Env) Then
                Dim P = System.IO.Path.Combine(Env, "bin", "java.exe")
                If FileUtils.Exists(P) Then Candidates.Add(P)
            End If
        Catch
        End Try

        If Candidates.Count = 0 Then Return Nothing

        Dim Best As String = Nothing
        Dim BestMajor As Integer = -1
        For Each P In Candidates.Distinct()
            Dim Major = GetJavaMajor(P)
            If Best Is Nothing OrElse Major > BestMajor Then
                Best = P
                BestMajor = Major
            End If
            If MinJavaMajor > 0 AndAlso Major >= MinJavaMajor Then Return P
        Next
        If MinJavaMajor > 0 AndAlso BestMajor < MinJavaMajor Then
            Logger.Warn($"服务端要求 Java {MinJavaMajor}+，但只找到 Java {BestMajor}（{Best}），启动很可能失败")
        End If
        Return Best
    End Function

    ''' <summary>读取某个 java.exe 的主版本号；失败返回 0。</summary>
    Public Function GetJavaMajor(JavaPath As String) As Integer
        Try
            If String.IsNullOrWhiteSpace(JavaPath) OrElse Not FileUtils.Exists(JavaPath) Then Return 0
            Dim Psi As New ProcessStartInfo With {
                .FileName = JavaPath, .Arguments = "-version",
                .UseShellExecute = False, .CreateNoWindow = True,
                .RedirectStandardError = True, .RedirectStandardOutput = True
            }
            Using P As Process = Process.Start(Psi)
                Dim Txt = P.StandardError.ReadToEnd() & P.StandardOutput.ReadToEnd()
                P.WaitForExit(3000)
                Dim M = Text.RegularExpressions.Regex.Match(Txt, "version ""(\d+)(?:\.(\d+))?")
                If Not M.Success Then Return 0
                Dim First = Integer.Parse(M.Groups(1).Value)
                If First = 1 AndAlso M.Groups(2).Success Then Return Integer.Parse(M.Groups(2).Value)
                Return First
            End Using
        Catch
            Return 0
        End Try
    End Function

    ''' <summary>按 MC 版本推算最低 Java 主版本。</summary>
    Private Function JavaMajorForMc(V As ServerVersion) As Integer
        Try
            If V Is Nothing OrElse String.IsNullOrWhiteSpace(V.Name) Then Return 17
            Dim Parts = V.Name.Split("."c)
            If Parts.Length = 0 Then Return 17
            Dim Maj As Integer = 0
            If Not Integer.TryParse(Parts(0), Maj) Then Return 17
            If Maj >= 26 Then Return 25
            If Maj = 1 AndAlso Parts.Length >= 2 Then
                Dim Min As Integer = 0
                Integer.TryParse(Parts(1), Min)
                If Min >= 20 Then Return 21
                If Min >= 17 Then Return 17
                Return 8
            End If
            Return 17
        Catch
            Return 17
        End Try
    End Function

    ''' <summary>构造启动参数。内存下限 1024，避免填 0 后起不来。</summary>
    Public Function BuildArguments(V As ServerVersion) As String
        Dim Mem = Math.Max(1024, V.MemoryMB)
        Dim Xms = Math.Min(512, Mem)
        '控制台乱码根因：Java 在 Windows 上默认用系统代码页（GBK）输出，
        '而我们按 UTF-8 解码。强制 Java 用 UTF-8 输出，两边编码就对齐了。
        '-Dfile.encoding 只管文件读写，-Dstdout.encoding/-Dstderr.encoding 才是 Java 18+ 的关键，
        '三个一起给，覆盖各版本差异。
        Return $"-Xms{Xms}M -Xmx{Mem}M " &
               "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 " &
               "-Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 " &
               $"-jar ""server.jar"" nogui"
    End Function

    '==== 运行状态 ====

    Public Property Running As Process = Nothing
    Public Property RunningVersion As ServerVersion = Nothing

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return Running IsNot Nothing AndAlso Not Running.HasExited
        End Get
    End Property

    ''' <summary>启动服务端。</summary>
    Public Function StartServer(V As ServerVersion) As Boolean
        Try
            If IsRunning Then
                Hint("已有服务端在运行，请先停止", HintType.Red)
                Return False
            End If
            If V Is Nothing Then Return False
            If Not V.EulaAccepted Then
                Hint("需要先同意 Minecraft EULA 才能开服", HintType.Red)
                Return False
            End If
            If Not FileUtils.Exists(V.JarPath) Then
                Hint("找不到 server.jar，请先下载服务端", HintType.Red)
                Return False
            End If

            Dim NeedMajor As Integer = JavaMajorForMc(V)
            Dim Java = FindJava(NeedMajor)
            If Java Is Nothing Then
                Hint("没有可用的 Java，请先在设置里安装 Java", HintType.Red)
                Return False
            End If
            Dim JavaMajor = GetJavaMajor(Java)
            If NeedMajor > 0 AndAlso JavaMajor > 0 AndAlso JavaMajor < NeedMajor Then
                Hint($"这个服务端需要 Java {NeedMajor} 或更高，但找到的是 Java {JavaMajor}。请到「设置 → 启动选项」添加更新的 Java。", HintType.Red, False)
                Return False
            End If
            Logger.Info($"服务端使用 Java {JavaMajor}（{Java}），要求 Java {NeedMajor}+")

            WriteProperties(V)

            Dim Psi As New ProcessStartInfo With {
                .FileName = Java,
                .Arguments = BuildArguments(V),
                .WorkingDirectory = V.Dir,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardInput = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            Dim Proc As New Process With {.StartInfo = Psi, .EnableRaisingEvents = True}
            AddHandler Proc.OutputDataReceived, Sub(sender, e)
                                                    If e.Data Is Nothing Then Return
                                                    Logger.Info($"[服务端 {V.Name}] {e.Data}")
                                                    Try
                                                        OutputHandler?.Invoke(e.Data)
                                                    Catch
                                                    End Try
                                                End Sub
            AddHandler Proc.ErrorDataReceived, Sub(sender, e)
                                                   If e.Data IsNot Nothing Then Logger.Warn($"[服务端 {V.Name}] {e.Data}")
                                               End Sub
            Proc.Start()
            Proc.BeginOutputReadLine()
            Proc.BeginErrorReadLine()

            '服务端起不来是常见投诉点：把 stderr 留下来并翻译成可读提示
            Dim LastErr As New Concurrent.ConcurrentQueue(Of String)
            AddHandler Proc.ErrorDataReceived,
                Sub(sender, e)
                    If e.Data Is Nothing Then Return
                    LastErr.Enqueue(e.Data)
                    While LastErr.Count > 20
                        Dim Drop As String = Nothing
                        LastErr.TryDequeue(Drop)
                    End While
                End Sub
            AddHandler Proc.Exited,
                Sub(sender, e)
                    Try
                        Dim Code = Proc.ExitCode
                        Running = Nothing
                        If Code = 0 Then
                            Logger.Info($"服务端正常退出：{V.Name}")
                            Return
                        End If
                        Dim Detail = String.Join(vbCrLf, LastErr.ToArray().Take(6))
                        Logger.Error($"服务端异常退出（代码 {Code}）：{V.Name}" & vbCrLf & Detail)
                        Dim Friendly As String
                        If Detail.Contains("requires Java") OrElse Detail.Contains("requires running the server with Java") Then
                            Friendly = "这个服务端需要更高版本的 Java。请到「设置 → 启动选项」添加新版本 Java 后重试。"
                        ElseIf Detail.Contains("SSLHandshakeException") OrElse Detail.Contains("SSL peer shut down") OrElse Detail.Contains("Failed to download") Then
                            Friendly = "服务端首次启动需要联网下载 Mojang 官方文件，但连接被中断。" & vbCrLf & "请检查网络或代理后重试（不需要重建服务端）。"
                        ElseIf Detail.Contains("Address already in use") Then
                            Friendly = $"端口 {V.Port} 已被占用，请换端口或关掉占用程序。"
                        ElseIf Detail.Contains("UnsupportedClassVersionError") Then
                            Friendly = "Java 版本太旧，无法运行这个服务端。"
                        ElseIf Detail.Contains("OutOfMemoryError") Then
                            Friendly = $"内存不足（当前 {V.MemoryMB} MB），请在服务端设置里调高。"
                        Else
                            Friendly = $"服务端启动后立即退出（代码 {Code}），详见控制台输出。"
                        End If
                        RunInUi(Sub() Hint(Friendly, HintType.Red, False))
                    Catch ex2 As Exception
                        Logger.Warn(ex2, "处理服务端退出事件失败")
                    End Try
                End Sub

            Running = Proc
            Running = Proc
            RunningVersion = V
            Logger.Info($"服务端已启动：{V.Name}，内存 {V.MemoryMB}MB，端口 {V.Port}")
            Hint($"服务端 {V.Name} 已启动", HintType.Green)
            Return True
        Catch ex As Exception
            Logger.Error(ex, "启动服务端失败")
            Hint($"启动失败：{ex.Message}", HintType.Red)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 一键开服：把「下载核心 → 同意 EULA → 写配置 → 启动」串成一条流程。
    ''' <para>
    ''' 设计取舍：中途任何一步失败都返回具体原因，不做「静默重试」——
    ''' 开服失败通常是网络或 Java 版本问题，重试解决不了，得让玩家看到真实原因。
    ''' </para>
    ''' </summary>
    ''' <param name="Core">要下载的核心。已经装好 server.jar 时传 Nothing。</param>
    ''' <param name="V">目标版本。目录不存在时按 Core 自动创建。</param>
    ''' <param name="Progress">进度回调，参数是 (阶段说明, 0-1 比例)。</param>
    Public Function OneClickStart(Core As ModServerCore.CoreFile,
                                  ByRef V As ServerVersion,
                                  Optional Progress As Action(Of String, Double) = Nothing) As String
        Try
            '—— 阶段 1：准备版本对象 ——
            Progress?.Invoke("准备服务端目录", 0.02)
            If V Is Nothing Then
                If Core Is Nothing Then Return "没有指定要下载的核心"
                Dim Name As String = $"{Core.McVersion}-{Core.CoreName}"
                V = LocalVersions.FirstOrDefault(Function(x) x.Name = Name)
                If V Is Nothing Then
                    V = New ServerVersion With {.Name = Name}
                    LocalVersions.Add(V)
                End If
            End If
            DirectoryUtils.Create(V.Dir)

            '—— 阶段 2：下载核心（已有就跳过）——
            If Not FileUtils.Exists(V.JarPath) Then
                If Core Is Nothing Then Return "找不到 server.jar，也没有指定要下载的核心"
                Progress?.Invoke($"下载 {Core.CoreName} {Core.McVersion}", 0.05)
                If Not DownloadCore(Core, V.Name) Then
                    Return $"服务端核心下载失败：{Core.CoreName} {Core.McVersion}。请检查网络后重试。"
                End If
                'DownloadCore 内部会 SaveConfig，这里重新读一次大小
                V.Installed = FileUtils.Exists(V.JarPath)
                If V.Installed Then V.JarSize = New IO.FileInfo(V.JarPath).Length
            End If
            Progress?.Invoke("核心就绪", 0.45)

            '—— 阶段 3：EULA ——
            'EULA 是法律文件，必须由玩家自己同意，不能替玩家点。
            If Not V.EulaAccepted Then
                Progress?.Invoke("等待同意 EULA", 0.5)
                Return "EULA_NOT_ACCEPTED"
            End If

            '—— 阶段 4：写配置 ——
            Progress?.Invoke("写入 server.properties", 0.55)
            WriteProperties(V)

            '—— 阶段 5：启动 ——
            Progress?.Invoke("启动服务端", 0.7)
            If Not StartServer(V) Then
                Return "启动失败。常见原因：Java 版本不匹配（1.17+ 需要 Java 17）、端口被占用、内存不足。详见日志。"
            End If

            Progress?.Invoke("已启动", 1.0)
            Return ""
        Catch ex As Exception
            Logger.Error(ex, "一键开服失败")
            Return $"一键开服出错：{ex.Message}"
        End Try
    End Function

    ''' <summary>向运行中的服务端发送一条控制台命令。</summary>
    Public Function SendCommand(Command As String) As Boolean
        Try
            If Not IsRunning Then Return False
            If String.IsNullOrWhiteSpace(Command) Then Return False
            Running.StandardInput.WriteLine(Command)
            Running.StandardInput.Flush()
            Logger.Info($"[服务端 {RunningVersion?.Name}] > {Command}")
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "发送服务端命令失败")
            Return False
        End Try
    End Function

    ''' <summary>控制台输出回调，由页面注册。</summary>
    Public Property OutputHandler As Action(Of String) = Nothing

    ''' <summary>优雅停止：先发 stop，15 秒超时再强杀。</summary>
    Public Sub StopServer()
        Try
            If Not IsRunning Then Return
            Dim Proc = Running
            Try
                Proc.StandardInput.WriteLine("stop")
                Proc.StandardInput.Flush()
                Logger.Info("已向服务端发送 stop 命令")
            Catch
            End Try
            If Not Proc.WaitForExit(15000) Then
                Logger.Warn("服务端未在 15 秒内退出，强制结束进程树")
                'java 会派生控制台守护等子进程，裸 Kill 只杀父进程会留孤儿
                '占用端口与文件句柄——用 taskkill /T 连整棵进程树一起终结
                Try
                    Dim Killer As New ProcessStartInfo With {
                        .FileName = "taskkill",
                        .Arguments = $"/PID {Proc.Id} /T /F",
                        .CreateNoWindow = True,
                        .UseShellExecute = False}
                    Process.Start(Killer).WaitForExit(5000)
                Catch ex2 As Exception
                    Logger.Warn(ex2, "taskkill 失败，回退到单进程 Kill")
                    Proc.Kill()
                End Try
            End If
            Running = Nothing
            RunningVersion = Nothing
            Hint("服务端已停止", HintType.Blue)
        Catch ex As Exception
            Logger.Error(ex, "停止服务端失败")
        End Try
    End Sub

    '==== HTTP ====

    Private Function NetGetString(Url As String) As String
        Try
            'Mojang 接口只支持 TLS 1.2+，.NET Framework 4.8 默认可能不带
            Net.ServicePointManager.SecurityProtocol = Net.ServicePointManager.SecurityProtocol Or
                                                       Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
            Dim Req As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Req.Method = "GET"
            Req.Timeout = 20000
            Req.ReadWriteTimeout = 20000
            Req.UserAgent = "SLATCL/" & VersionBaseName
            Req.AutomaticDecompression = Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                Using Reader As New IO.StreamReader(Resp.GetResponseStream(), Encoding.UTF8)
                    Return Reader.ReadToEnd()
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"请求失败：{Url}")
            Return Nothing
        End Try
    End Function

    ''' <summary>下载进度回调：参数为 0~1 的比例，或 -1 表示总量未知。</summary>
    Public Property DownloadProgress As Action(Of Double) = Nothing  ' 下载进度回调

    Private Function NetGetBytes(Url As String, Optional Loader As LoaderBase = Nothing) As Byte()
        Try
            'Mojang 接口只支持 TLS 1.2+，.NET Framework 4.8 默认可能不带
            Net.ServicePointManager.SecurityProtocol = Net.ServicePointManager.SecurityProtocol Or
                                                        Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
            Dim Req As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Req.Method = "GET"
            '63MB 在国内网络下可能超过 5 分钟，给足 10 分钟
            Req.Timeout = 600000
            Req.ReadWriteTimeout = 600000
            'FastMirror 可能重定向到 CDN，必须允许
            Req.AllowAutoRedirect = True
            Req.MaximumAutomaticRedirections = 10
            Req.UserAgent = "SLATCL/" & VersionBaseName
            Req.Accept = "*/*"
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                Dim Total = Resp.ContentLength
                Logger.Info($"服务端下载开始：HTTP {CInt(Resp.StatusCode)}，长度={Total}，类型={Resp.ContentType}")
                Using Src = Resp.GetResponseStream()
                    Using Ms As New IO.MemoryStream()
                        Dim Buf(81919) As Byte
                        Dim Read As Integer
                        Dim WarnCount As Integer = 0
                        Do
                            Read = Src.Read(Buf, 0, Buf.Length)
                            If Read <= 0 Then Exit Do
                            Ms.Write(Buf, 0, Read)
                            If Loader IsNot Nothing AndAlso Total > 0 Then
                                Loader.Progress = CDbl(Ms.Length) / Total
                            End If
                            If DownloadProgress IsNot Nothing Then
                                Dim Ratio As Double = -1.0
                                If Total > 0 Then Ratio = CDbl(Ms.Length) / CDbl(Total)
                                Try
                                    DownloadProgress.Invoke(Ratio)
                                Catch ex2 As Exception
                                    '进度回调不能影响下载本身
                                    If WarnCount < 3 Then
                                        Logger.Warn(ex2, "进度回调失败")
                                        WarnCount += 1
                                    End If
                                End Try
                            End If
                        Loop
                        Dim Result = Ms.ToArray()
                        Logger.Info($"服务端下载完成：{StringUtils.FormatByteSize(Result.Length)}")
                        Return Result
                    End Using
                End Using
            End Using
        Catch ex As Net.WebException
            '把 HTTP 状态码与最终地址记清楚，否则排查时两眼一抹黑
            Logger.Warn(ex, $"下载失败（WebException）：{Url}")
            If ex.Response IsNot Nothing Then
                Try
                    Dim HR = CType(ex.Response, Net.HttpWebResponse)
                    Logger.Warn($"  HTTP {CInt(HR.StatusCode)}，最终地址：{HR.ResponseUri}")
                Catch
                End Try
            End If
            Logger.Warn($"  状态：{ex.Status}")
            Return Nothing
        Catch ex As Exception
            Logger.Warn(ex, $"下载失败：{Url}")
            Return Nothing
        End Try
    End Function


    ''' <summary>
    ''' 下载一个服务端核心到指定版本目录，并建立版本配置。
    ''' </summary>
    Public Function DownloadCore(File As ModServerCore.CoreFile, VersionName As String) As Boolean
        'SeaLantern 成熟逻辑对齐：下载后按 FastMirror 提供的 sha1 校验完整性，
        '校验失败删掉残包并报错，避免半截 jar 进启动流程
        Return DownloadCore(File, VersionName, True)
    End Function

    Public Function DownloadCore(File As ModServerCore.CoreFile, VersionName As String, VerifySha1 As Boolean) As Boolean
        Try
            If File Is Nothing OrElse String.IsNullOrWhiteSpace(File.Url) Then
                Hint("没有可用的下载地址", HintType.Red)
                Return False
            End If
            If String.IsNullOrWhiteSpace(VersionName) Then Return False

            Dim Dir = System.IO.Path.Combine(ServerDir, VersionName)
            DirectoryUtils.Create(Dir)
            Dim Target = System.IO.Path.Combine(Dir, "server.jar")
            Dim Temp = Target & ".download"

            '把完整 URL 和参数记下来，出问题能直接定位
            Logger.Info($"下载服务端核心：{File.CoreName} {File.McVersion} {File.Build}")
            Logger.Info($"  目标目录：{Dir}")
            Logger.Info($"  下载地址：{File.Url}")
            Logger.Info($"  期望 SHA1：{If(String.IsNullOrWhiteSpace(File.Sha1), "(无)", File.Sha1)}")
            Dim Data As Byte() = NetGetBytes(File.Url, Nothing)
            If Data Is Nothing Then
                Hint("服务端下载失败：网络请求没有返回数据，详见日志", HintType.Red)
                Return False
            End If
            '原版服务端 jar 可能只有几百 KB，1MB 的硬门槛会误判。
            '改为：只要不是明显错误页（< 8KB）就放行，交给后面的 PK 头和 SHA1 校验。
            'SHA1 完整性校验（成熟逻辑对齐）：FastMirror 提供 sha1，下载后必验
            If VerifySha1 AndAlso Not String.IsNullOrWhiteSpace(File.Sha1) Then
                Using Sha As New Security.Cryptography.SHA1CryptoServiceProvider()
                    Dim Hash = BitConverter.ToString(Sha.ComputeHash(Data)).Replace("-", "").ToLowerInvariant()
                    If Hash <> File.Sha1.ToLowerInvariant() Then
                        Hint($"服务端核心校验失败（SHA1 不匹配），已放弃。请重试或换下载源。", HintType.Red)
                        Logger.Warn($"SHA1 校验失败：期望 {File.Sha1} 实际 {Hash}")
                        Return False
                    End If
                    Logger.Info("SHA1 校验通过")
                End Using
            End If

            If Data.Length < 8192 Then
                Dim Preview As String = ""
                Try
                    Preview = Encoding.UTF8.GetString(Data, 0, Math.Min(200, Data.Length))
                Catch
                End Try
                Hint($"服务端下载失败：只拿到 {Data.Length} 字节，可能是错误页", HintType.Red)
                Logger.Warn($"下载内容过小（{Data.Length} B），前 200 字符：{Preview}")
                Return False
            End If

            '校验：jar 是 zip，头两字节应为 PK
            If Data.Length < 2 OrElse Data(0) <> &H50 OrElse Data(1) <> &H4B Then
                Hint("下载到的不是有效的服务端 jar", HintType.Red)
                Return False
            End If

            '有 SHA1 就校验
            If Not String.IsNullOrWhiteSpace(File.Sha1) Then
                Try
                    Using Sha = Security.Cryptography.SHA1.Create()
                        Dim Actual = BitConverter.ToString(Sha.ComputeHash(Data)).Replace("-", "").ToLowerInvariant()
                        If Not String.Equals(Actual, File.Sha1.Trim(), StringComparison.OrdinalIgnoreCase) Then
                            Hint("下载校验失败，文件可能损坏，请重试", HintType.Red)
                            Logger.Warn($"SHA1 不匹配：期望 {File.Sha1}，实际 {Actual}")
                            Return False
                        End If
                        Logger.Info("SHA1 校验通过")
                    End Using
                Catch ex As Exception
                    Logger.Warn(ex, "SHA1 校验出错，跳过")
                End Try
            End If

            IO.File.WriteAllBytes(Temp, Data)
            If FileUtils.Exists(Target) Then FileUtils.Delete(Target, True)
            IO.File.Move(Temp, Target)

            Dim V As New ServerVersion With {.Name = VersionName, .Installed = True, .JarSize = Data.Length}
            SaveConfig(V)
            Logger.Info($"服务端已保存：{Target}（{StringUtils.FormatByteSize(Data.Length)}）")
            Hint($"服务端已下载：{File.CoreName} {File.McVersion}", HintType.Green)
            Try
                DownloadProgress?.Invoke(1.0)
            Catch
            End Try
            Return True
        Catch ex As Exception
            Logger.Error(ex, "下载服务端核心失败")
            Hint($"下载失败：{ex.Message}", HintType.Red)
            Return False
        End Try
    End Function

End Module
