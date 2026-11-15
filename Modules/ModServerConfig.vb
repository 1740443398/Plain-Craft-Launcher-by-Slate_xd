Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Collections.Generic
Imports System.Linq
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq

''' <summary>
''' 开服页的配置扩展：备份 / 恢复、白名单管理、服务端文件补全，以及配置面板的构建与回读。
''' <para>
''' <b>为什么单独拆一个模块</b>：<c>ModServer</c> 负责「起服」这条主干（扫描、下载、EULA、启动、停服），
''' 这里负责「服开起来之后怎么管」。两者失败模式完全不同：主干失败是网络和 Java 的问题，
''' 这里失败基本是文件被占用或玩家填了非法值，分开写才好分别定位。
''' </para>
''' <para>
''' <b>关于 RCON</b>：这里只负责把 <c>enable-rcon</c> / <c>rcon.port</c> / <c>rcon.password</c>
''' 正确写进 server.properties。真正的 RCON 协议客户端（走 TCP 发 Source RCON 包）不在这里做，
''' 因为 PCL 已经有控制台 stdin 通道，同机能直接发命令，再叠一层 TCP 客户端只会多一条出错路径。
''' </para>
''' </summary>
Partial Public Module ModServer

    '==== 备份 ====

    ''' <summary>所有服务端备份的根目录。</summary>
    Public ReadOnly Property ServerBackupRoot As String
        Get
            Return Paths.Base & "PCL\Servers\.backups\"
        End Get
    End Property

    ''' <summary>某个版本的备份目录：<c>PCL\Servers\.backups\&lt;版本名&gt;\</c>。</summary>
    Public Function BackupDirFor(V As ServerVersion) As String
        If V Is Nothing Then Return ""
        Return System.IO.Path.Combine(ServerBackupRoot, SafeFileName(V.Name))
    End Function

    ''' <summary>把任意文本变成安全的文件名片段。</summary>
    Private Function SafeFileName(Raw As String) As String
        If String.IsNullOrWhiteSpace(Raw) Then Return "_"
        Dim Invalid = System.IO.Path.GetInvalidFileNameChars()
        Dim Sb As New StringBuilder()
        For Each C In Raw.Trim()
            If Array.IndexOf(Invalid, C) >= 0 Then
                Sb.Append("_"c)
            Else
                Sb.Append(C)
            End If
        Next
        Dim Result = Sb.ToString().Trim()
        If Result.Length = 0 Then Return "_"
        Return Result
    End Function

    ''' <summary>
    ''' 收集要备份的内容，返回相对 <c>V.Dir</c> 的路径（目录只返回顶层目录名）。
    ''' <para>
    ''' 备份范围刻意排除了 <c>logs\</c>、<c>crash-reports\</c>、<c>cache\</c> 和各个核心自己的
    ''' 缓存目录：这些东西每次启动都变，打进去会让备份体积翻几倍却没有任何恢复价值。
    ''' </para>
    ''' </summary>
    Public Function CollectBackupSources(V As ServerVersion) As List(Of String)
        Dim Result As New List(Of String)
        Try
            If V Is Nothing OrElse Not DirectoryUtils.Exists(V.Dir) Then Return Result
            Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            '世界目录：level-name 指定的那个，加上所有 world* 开头的
            '（原版会自动生成 world_nether / world_the_end，改过 level-name 时也要能认出来）
            Dim Worlds As New List(Of String)
            If Not String.IsNullOrWhiteSpace(V.LevelName) Then Worlds.Add(V.LevelName.Trim())
            Worlds.Add("world")
            Try
                For Each D In Directory.GetDirectories(V.Dir)
                    Dim N = System.IO.Path.GetFileName(D)
                    If N.StartsWith("world", StringComparison.OrdinalIgnoreCase) Then Worlds.Add(N)
                Next
            Catch ex As Exception
                Logger.Warn(ex, "枚举世界目录失败")
            End Try
            For Each Name In Worlds
                If String.IsNullOrWhiteSpace(Name) Then Continue For
                If DirectoryUtils.Exists(System.IO.Path.Combine(V.Dir, Name)) AndAlso Seen.Add(Name) Then Result.Add(Name)
            Next

            '根目录下的关键配置文件
            Dim Files = {
                "server.properties", "eula.txt", "ops.json", "whitelist.json",
                "banned-players.json", "banned-ips.json", "pcl-server.json"
            }
            For Each F In Files
                If FileUtils.Exists(System.IO.Path.Combine(V.Dir, F)) AndAlso Seen.Add(F) Then Result.Add(F)
            Next

            '整目录：插件/模组配置
            Dim Folders = {"config", "mods"}
            For Each D In Folders
                If DirectoryUtils.Exists(System.IO.Path.Combine(V.Dir, D)) AndAlso Seen.Add(D) Then Result.Add(D)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "收集备份内容失败")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 备份一个服务端，返回生成的 zip 完整路径；失败返回 <c>Nothing</c>。
    ''' <para>
    ''' 命名是 <c>&lt;yyyyMMdd-HHmmss&gt;.zip</c>，同一秒内重复备份会自动加序号；
    ''' 带 Note 时拼成 <c>&lt;时间戳&gt;_&lt;备注&gt;.zip</c>，这样不用额外的清单文件也能看懂是什么时候、为什么备份的。
    ''' </para>
    ''' </summary>
    ''' <param name="V">要备份的服务端版本。</param>
    ''' <param name="Note">备注，会进文件名。留空则只有时间戳。</param>
    Public Function BackupServer(V As ServerVersion, Optional Note As String = "") As String
        Try
            If V Is Nothing OrElse String.IsNullOrWhiteSpace(V.Name) Then Return Nothing
            If Not DirectoryUtils.Exists(V.Dir) Then
                Logger.Warn($"备份失败：服务端目录不存在（{V.Dir}）")
                Return Nothing
            End If

            Dim Sources = CollectBackupSources(V)
            If Sources.Count = 0 Then
                Logger.Warn($"备份失败：{V.Name} 下没有可备份的内容")
                Return Nothing
            End If

            Dim Dir = BackupDirFor(V)
            DirectoryUtils.Create(Dir)

            '时间戳精确到毫秒：恢复流程里「恢复前自动备份」和玩家刚点的「手动备份」
            '经常落在同一秒，只到秒的话两份文件名除了备注完全一样，
            '排序和「哪份更新」就无从判断了。毫秒后缀让文件名本身就是全序。
            Dim Now = DateTime.Now
            Dim Stamp = Now.ToString("yyyyMMdd-HHmmss") & "-" & Now.Millisecond.ToString("000")
            Dim Suffix = ""
            If Not String.IsNullOrWhiteSpace(Note) Then Suffix = "_" & SafeFileName(Note)
            Dim Target = System.IO.Path.Combine(Dir, Stamp & Suffix & ".zip")
            Dim Index As Integer = 1
            While FileUtils.Exists(Target) AndAlso Index <= 999
                Target = System.IO.Path.Combine(Dir, $"{Stamp}{Suffix}({Index}).zip")
                Index += 1
            End While

            '先写 .part 再改名：备份中途断电/崩溃时不会留下一个看起来正常、其实打不开的 zip
            Dim Temp = Target & ".part"
            Try
                If FileUtils.Exists(Temp) Then FileUtils.Delete(Temp, True)
                Using Fs As New FileStream(Temp, FileMode.Create, FileAccess.Write)
                    Using Za As New ZipArchive(Fs, ZipArchiveMode.Create)
                        For Each Rel In Sources
                            AddToArchive(Za, V.Dir, Rel)
                        Next
                    End Using
                End Using
            Catch ex As Exception
                Logger.Warn(ex, $"写备份压缩包失败：{Target}")
                Try
                    If FileUtils.Exists(Temp) Then FileUtils.Delete(Temp, True)
                Catch
                End Try
                Return Nothing
            End Try

            If FileUtils.Exists(Target) Then FileUtils.Delete(Target, True)
            IO.File.Move(Temp, Target)

            Dim Size As Long = 0
            Try
                Size = New FileInfo(Target).Length
            Catch
            End Try
            Logger.Info($"服务端已备份：{Target}（{StringUtils.FormatByteSize(Size)}，{Sources.Count} 个顶层项）")
            Return Target
        Catch ex As Exception
            Logger.Error(ex, "备份服务端失败")
            Return Nothing
        End Try
    End Function

    ''' <summary>把一个相对路径（文件或目录）加入压缩包。</summary>
    Private Sub AddToArchive(Za As ZipArchive, Root As String, Rel As String)
        Dim Full = System.IO.Path.Combine(Root, Rel)
        If DirectoryUtils.Exists(Full) Then
            Dim Files As String() = Nothing
            Try
                Files = Directory.GetFiles(Full, "*", SearchOption.AllDirectories)
            Catch ex As Exception
                Logger.Warn(ex, $"枚举目录失败，跳过：{Rel}")
                Return
            End Try
            For Each F In Files
                Dim RelFile = F.Substring(Root.Length).TrimStart("\"c)
                AddFileEntry(Za, F, RelFile)
            Next
        ElseIf FileUtils.Exists(Full) Then
            AddFileEntry(Za, Full, Rel)
        End If
    End Sub

    ''' <summary>写入单个文件条目。读不到的文件跳过，不能让一个被占用的文件毁掉整次备份。</summary>
    Private Sub AddFileEntry(Za As ZipArchive, Full As String, EntryName As String)
        Try
            Dim Entry = Za.CreateEntry(EntryName.Replace("\"c, "/"c), CompressionLevel.Optimal)
            '变量名不能用 In / Out：两个都是 VB 关键字
            Using ZipStream = Entry.Open()
                'FileShare.ReadWrite：服务端正在跑的时候日志/存档可能被独占，尽量还能读出来
                Using Src As New FileStream(Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Src.CopyTo(ZipStream)
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"跳过无法读取的文件：{EntryName}")
        End Try
    End Sub

    ''' <summary>
    ''' 列出某个版本的所有备份，最新的在前。
    ''' <para>
    ''' 排序以文件写入时间为准，文件名只作次级依据。原因是文件名里的时间戳只到秒：
    ''' 「手动备份」和恢复前自动生成的「恢复前自动备份」很可能落在同一秒，
    ''' 光看名字分不出谁更晚；而 <c>(n)</c> 的码位又小于 <c>.</c>，纯按名字倒序还会把更新的排到后面。
    ''' </para>
    ''' </summary>
    Public Function ListBackups(V As ServerVersion) As List(Of String)
        Dim Result As New List(Of String)
        Try
            Dim Dir = BackupDirFor(V)
            If String.IsNullOrWhiteSpace(Dir) OrElse Not DirectoryUtils.Exists(Dir) Then Return Result
            Result = Directory.GetFiles(Dir, "*.zip").
                      OrderByDescending(Function(F) LastWriteTicks(F)).
                      ThenByDescending(Function(F) BackupSequence(F)).
                      ThenByDescending(Function(F) System.IO.Path.GetFileName(F), StringComparer.Ordinal).
                      ToList()
        Catch ex As Exception
            Logger.Warn(ex, "列出服务端备份失败")
        End Try
        Return Result
    End Function

    ''' <summary>文件最后写入时间；读不到返回 0，让它沉到列表底部而不是抛异常。</summary>
    Private Function LastWriteTicks(Path As String) As Long
        Try
            Return IO.File.GetLastWriteTime(Path).Ticks
        Catch ex As Exception
            Return 0
        End Try
    End Function

    ''' <summary>同一秒内重复备份时文件名末尾的 <c>(n)</c> 序号，没有则为 0。</summary>
    Private Function BackupSequence(ZipPath As String) As Integer
        Try
            Dim BaseName = System.IO.Path.GetFileNameWithoutExtension(ZipPath)
            Dim M = Text.RegularExpressions.Regex.Match(BaseName, "\((\d+)\)$")
            If Not M.Success Then Return 0
            Dim N As Integer = 0
            If Integer.TryParse(M.Groups(1).Value, N) Then Return N
            Return 0
        Catch ex As Exception
            Return 0
        End Try
    End Function

    ''' <summary>备份文件的可读名字（去掉目录和扩展名）。</summary>
    Public Function BackupDisplayName(ZipPath As String) As String
        If String.IsNullOrWhiteSpace(ZipPath) Then Return ""
        Try
            Return System.IO.Path.GetFileNameWithoutExtension(ZipPath)
        Catch
            Return ZipPath
        End Try
    End Function

    ''' <summary>删除一个备份文件。</summary>
    ''' <summary>
    ''' 每日自动备份：有世界存档且距上次备份超过 24 小时才触发。
    ''' 在 ScanLocal 后调用一次即可；失败静默，只记日志。
    ''' </summary>
    Public Sub AutoBackupIfDue()
        Try
            For Each V In ModServer.LocalVersions
                If Not V.Installed Then Continue For
                If Not IO.Directory.Exists(IO.Path.Combine(V.Dir, "world")) Then Continue For
                Dim Backups = ListBackups(V)
                Dim Latest = Backups.OrderByDescending(Function(x) x).FirstOrDefault()
                If Latest IsNot Nothing AndAlso (Date.Now - IO.File.GetLastWriteTime(Latest)).TotalHours < 24 Then Continue For
                Dim ZipPath = BackupServer(V, "自动备份")
                If ZipPath IsNot Nothing Then
                    Logger.Info($"自动备份完成：{V.Name} -> {ZipPath}")
                Else
                    Logger.Warn($"自动备份失败：{V.Name}")
                End If
            Next
        Catch ex As Exception
            Logger.Warn(ex, "每日自动备份失败")
        End Try
    End Sub

    Public Function DeleteBackup(ZipPath As String) As Boolean
        Try
            If String.IsNullOrWhiteSpace(ZipPath) OrElse Not FileUtils.Exists(ZipPath) Then Return False
            FileUtils.Delete(ZipPath, True)
            Logger.Info($"已删除服务端备份：{ZipPath}")
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"删除备份失败：{ZipPath}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 从备份恢复，返回错误信息；空串表示成功。
    ''' <para>
    ''' <b>先备份再恢复</b>：恢复本身是破坏性操作（会覆盖当前世界），如果玩家选错了备份，
    ''' 没有这一步就永远回不来了。所以哪怕恢复失败，也至少留了一份「恢复前」的快照。
    ''' </para>
    ''' <para>
    ''' <b>为什么解压到临时目录再拷回去</b>：<c>ZipFile.ExtractToDirectory</c> 遇到已存在的文件会直接抛异常，
    ''' 而恢复的语义本来就是「覆盖」。先解到临时目录，再逐个覆盖拷贝，既能覆盖又不会半途炸掉。
    ''' </para>
    ''' </summary>
    Public Function RestoreServer(V As ServerVersion, ZipPath As String) As String
        Try
            If V Is Nothing OrElse String.IsNullOrWhiteSpace(V.Name) Then Return "没有指定服务端版本"
            If String.IsNullOrWhiteSpace(ZipPath) Then Return "没有选择要恢复的备份"
            If Not FileUtils.Exists(ZipPath) Then Return $"备份文件不存在：{ZipPath}"
            If ModServer.IsRunning Then Return "服务端正在运行，请先停止再恢复备份"

            DirectoryUtils.Create(V.Dir)

            Dim Safety = BackupServer(V, "恢复前自动备份")
            If Safety Is Nothing Then
                Logger.Warn("恢复前的自动备份失败，仍继续恢复")
            Else
                Logger.Info($"恢复前已自动备份：{Safety}")
            End If

            Dim Temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcl-restore-" & Guid.NewGuid().ToString("N"))
            Try
                DirectoryUtils.Create(Temp)
                Try
                    ZipFile.ExtractToDirectory(ZipPath, Temp)
                Catch ex As Exception
                    Logger.Warn(ex, $"解压备份失败：{ZipPath}")
                    Return $"解压备份失败：{ex.Message}"
                End Try

                Dim Count As Integer = 0
                For Each F In Directory.GetFiles(Temp, "*", SearchOption.AllDirectories)
                    Dim Rel = F.Substring(Temp.Length).TrimStart("\"c)
                    Dim Dest = System.IO.Path.Combine(V.Dir, Rel)
                    Dim DestDir = System.IO.Path.GetDirectoryName(Dest)
                    If Not String.IsNullOrEmpty(DestDir) Then DirectoryUtils.Create(DestDir)
                    IO.File.Copy(F, Dest, True)
                    Count += 1
                Next

                '恢复完要让内存里的状态跟上：EULA 可能从 false 变成了 true
                V.EulaAccepted = FileUtils.Exists(V.EulaPath) AndAlso
                                 FileUtils.ReadAsString(V.EulaPath).Contains("eula=true")
                ModServer.SaveConfig(V)

                Logger.Info($"已从备份恢复 {Count} 个文件：{ZipPath}")
                Return ""
            Finally
                Try
                    If DirectoryUtils.Exists(Temp) Then Directory.Delete(Temp, True)
                Catch ex As Exception
                    Logger.Warn(ex, "清理恢复临时目录失败")
                End Try
            End Try
        Catch ex As Exception
            Logger.Error(ex, "恢复服务端备份失败")
            Return $"恢复失败：{ex.Message}"
        End Try
    End Function

    '==== 白名单 ====

    ''' <summary>whitelist.json 的路径。</summary>
    Public Function WhitelistFile(V As ServerVersion) As String
        If V Is Nothing Then Return ""
        Return System.IO.Path.Combine(V.Dir, "whitelist.json")
    End Function

    ''' <summary>
    ''' 读白名单里的玩家名。
    ''' <para>文件不存在、内容为空、或者 JSON 坏了都返回空列表并记日志——不能让一个坏文件把配置页卡住。</para>
    ''' </summary>
    Public Function ReadWhitelist(V As ServerVersion) As List(Of String)
        Dim Result As New List(Of String)
        Try
            If V Is Nothing Then Return Result
            Dim P = WhitelistFile(V)
            If Not FileUtils.Exists(P) Then Return Result
            Dim Raw = FileUtils.ReadAsString(P)
            If String.IsNullOrWhiteSpace(Raw) Then Return Result

            Dim Arr As JArray = Nothing
            Try
                Arr = JArray.Parse(Raw)
            Catch ex As Exception
                Logger.Warn(ex, "whitelist.json 格式不正确，按空名单处理")
                Return Result
            End Try
            If Arr Is Nothing Then Return Result

            For Each Item In Arr
                Dim Obj = TryCast(Item, JObject)
                If Obj Is Nothing Then Continue For
                Dim N = Obj("name")
                If N Is Nothing Then Continue For
                Dim Text = N.ToString().Trim()
                If Text.Length > 0 Then Result.Add(Text)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "读取白名单失败")
        End Try
        Return Result
    End Function

    ''' <summary>把白名单文件读成 JArray；文件缺失或损坏时返回空数组。</summary>
    Private Function ReadWhitelistArray(V As ServerVersion) As JArray
        Try
            Dim P = WhitelistFile(V)
            If Not FileUtils.Exists(P) Then Return New JArray()
            Dim Raw = FileUtils.ReadAsString(P)
            If String.IsNullOrWhiteSpace(Raw) Then Return New JArray()
            Dim Parsed = JArray.Parse(Raw)
            If Parsed Is Nothing Then Return New JArray()
            Return Parsed
        Catch ex As Exception
            Logger.Warn(ex, "whitelist.json 无法解析，将重新生成")
            Return New JArray()
        End Try
    End Function

    Private Sub WriteWhitelistArray(V As ServerVersion, Arr As JArray)
        If V Is Nothing OrElse Arr Is Nothing Then Return
        DirectoryUtils.Create(V.Dir)
        '不带 BOM：Java 侧的 JSON 解析对 BOM 很敏感，Minecraft 自己写出来的也是无 BOM 的
        FileUtils.Write(WhitelistFile(V), Arr.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
    End Sub

    ''' <summary>
    ''' 由玩家名推导离线 UUID，算法与 Java 的 <c>UUID.nameUUIDFromBytes("OfflinePlayer:" + name)</c> 一致。
    ''' <para>
    ''' 为什么必须对：白名单里的 uuid 决定了玩家的身份。如果和原版算出来的不一样，
    ''' 正版验证关闭的服务器会认为这是另一个人，白名单就失效了。
    ''' </para>
    ''' </summary>
    Public Function OfflineUuid(PlayerName As String) As String
        Try
            Dim Name = If(PlayerName, "").Trim()
            Using Md5 = Security.Cryptography.MD5.Create()
                Dim Hash = Md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" & Name))
                '版本位 = 3（MD5 名字 UUID），变体位 = IETF
                Hash(6) = CByte((Hash(6) And &HF) Or &H30)
                Hash(8) = CByte((Hash(8) And &H3F) Or &H80)
                Dim Hex = BitConverter.ToString(Hash).Replace("-", "").ToLowerInvariant()
                Return Hex.Substring(0, 8) & "-" & Hex.Substring(8, 4) & "-" & Hex.Substring(12, 4) & "-" &
                       Hex.Substring(16, 4) & "-" & Hex.Substring(20, 12)
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "计算离线 UUID 失败")
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 把玩家加入白名单。
    ''' <para>
    ''' 服务端在跑时优先走 <c>whitelist add</c> 控制台命令：服务端会把名单读进内存并自己重写文件，
    ''' 这样不会出现「文件改了但服务端还在用旧名单」的不一致。服务端没跑时直接改文件。
    ''' </para>
    ''' </summary>
    Public Function AddToWhitelist(V As ServerVersion, PlayerName As String) As Boolean
        Try
            If V Is Nothing Then Return False
            Dim Name = If(PlayerName, "").Trim()
            If Name.Length = 0 Then Return False
            If Name.Contains(" ") Then
                Hint("玩家名不能包含空格", HintType.Red)
                Return False
            End If

            If IsServerRunning(V) Then
                If ModServer.SendCommand($"whitelist add {Name}") Then
                    Logger.Info($"已向运行中的服务端发送白名单添加：{Name}")
                    Return True
                End If
            End If

            Dim Current = ReadWhitelist(V)
            If Current.Any(Function(X) String.Equals(X, Name, StringComparison.OrdinalIgnoreCase)) Then
                Logger.Info($"白名单里已经有 {Name}，无需重复添加")
                Return True
            End If

            Dim Arr = ReadWhitelistArray(V)
            Dim Entry As New JObject()
            Entry("uuid") = OfflineUuid(Name)
            Entry("name") = Name
            Arr.Add(Entry)
            WriteWhitelistArray(V, Arr)
            Logger.Info($"已加入白名单：{Name}")
            Return True
        Catch ex As Exception
            Logger.Error(ex, "添加白名单失败")
            Return False
        End Try
    End Function

    ''' <summary>把玩家从白名单移除。</summary>
    Public Function RemoveFromWhitelist(V As ServerVersion, PlayerName As String) As Boolean
        Try
            If V Is Nothing Then Return False
            Dim Name = If(PlayerName, "").Trim()
            If Name.Length = 0 Then Return False

            If IsServerRunning(V) Then
                If ModServer.SendCommand($"whitelist remove {Name}") Then
                    Logger.Info($"已向运行中的服务端发送白名单移除：{Name}")
                    Return True
                End If
            End If

            Dim Arr = ReadWhitelistArray(V)
            Dim Keep As New JArray()
            Dim Removed As Integer = 0
            For Each Item In Arr
                Dim Obj = TryCast(Item, JObject)
                If Obj Is Nothing Then Continue For
                Dim N = Obj("name")
                Dim Text = If(N Is Nothing, "", N.ToString().Trim())
                If String.Equals(Text, Name, StringComparison.OrdinalIgnoreCase) Then
                    Removed += 1
                    Continue For
                End If
                Keep.Add(Item)
            Next
            If Removed = 0 Then
                Logger.Info($"白名单里没有 {Name}")
                Return False
            End If

            WriteWhitelistArray(V, Keep)
            Logger.Info($"已从白名单移除：{Name}")
            Return True
        Catch ex As Exception
            Logger.Error(ex, "移除白名单失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 判断当前运行的到底是不是这个版本。
    ''' <para>同时开着两个服务端目录时，把命令发给「另一个」服务端是真实的踩坑点，必须比对版本。</para>
    ''' </summary>
    Private Function IsServerRunning(V As ServerVersion) As Boolean
        If Not ModServer.IsRunning Then Return False
        If V Is Nothing Then Return False
        Dim Cur = ModServer.RunningVersion
        If Cur Is Nothing Then Return False
        Return String.Equals(Cur.Name, V.Name, StringComparison.OrdinalIgnoreCase)
    End Function

    '==== 服务端文件补全 ====

    ''' <summary>默认 eula.txt 内容。刻意写 false——EULA 是法律文件，不能替玩家同意。</summary>
    Private Function DefaultEulaText() As String
        Return "# 由 PCL 生成：还没有同意 Minecraft EULA。" & vbCrLf &
               "# 请先阅读 https://aka.ms/MinecraftEULA" & vbCrLf &
               "# 同意后把下面的 false 改成 true，或者回到 PCL 开服页点同意 EULA。" & vbCrLf &
               "eula=false" & vbCrLf
    End Function

    ''' <summary>
    ''' 检查并补全缺失的服务端文件，返回补了哪些（文件名列表，空列表表示本来就齐全）。
    ''' <para>
    ''' 补的东西：<c>eula.txt</c>、<c>server.properties</c>、<c>whitelist.json</c>、<c>ops.json</c>、
    ''' <c>banned-players.json</c>、<c>banned-ips.json</c>。
    ''' 已有的文件一律不覆盖——玩家手写的配置比我们的默认值重要得多。
    ''' </para>
    ''' </summary>
    Public Function RepairServerFiles(V As ServerVersion) As List(Of String)
        Dim Fixed As New List(Of String)
        Try
            If V Is Nothing OrElse String.IsNullOrWhiteSpace(V.Name) Then Return Fixed
            DirectoryUtils.Create(V.Dir)

            'eula.txt：只补成 eula=false，把「要不要同意」留给玩家
            If Not FileUtils.Exists(V.EulaPath) Then
                FileUtils.Write(V.EulaPath, DefaultEulaText(), New UTF8Encoding(False))
                Fixed.Add("eula.txt")
            End If

            'server.properties：缺文件时按当前配置生成一份
            Dim PropPath = System.IO.Path.Combine(V.Dir, "server.properties")
            If Not FileUtils.Exists(PropPath) Then
                ModServer.WriteProperties(V)
                If FileUtils.Exists(PropPath) Then Fixed.Add("server.properties")
            End If

            '这几个 JSON 服务端首次启动会自己生成，但提前放一个空的能让玩家在开服前就编辑
            Dim JsonFiles = {"whitelist.json", "ops.json", "banned-players.json", "banned-ips.json"}
            For Each Name In JsonFiles
                Dim P = System.IO.Path.Combine(V.Dir, Name)
                Dim NeedWrite As Boolean = False
                If Not FileUtils.Exists(P) Then
                    NeedWrite = True
                Else
                    '0 字节的 JSON 等同于损坏，解析会直接失败
                    Try
                        If New FileInfo(P).Length = 0 Then NeedWrite = True
                    Catch
                    End Try
                End If
                If NeedWrite Then
                    FileUtils.Write(P, "[]", New UTF8Encoding(False))
                    Fixed.Add(Name)
                End If
            Next

            If Fixed.Count > 0 Then
                Logger.Info($"已补全服务端文件：{String.Join(", ", Fixed)}")
            Else
                Logger.Info($"服务端文件齐全，无需补全：{V.Name}")
            End If
        Catch ex As Exception
            Logger.Error(ex, "补全服务端文件失败")
        End Try
        Return Fixed
    End Function

    '==== 配置面板 ====

    ''' <summary>
    ''' 构建服务端配置面板，返回一个可直接塞进容器的 StackPanel。
    ''' <para>
    ''' <b>分组折叠</b>：39 个配置项平铺会让人直接放弃改配置，所以按用途分成 7 组，
    ''' 默认只展开「基础」，其余折叠。折叠状态不落盘——每次打开都从默认状态开始，避免上次折叠过的组被藏起来找不到。
    ''' </para>
    ''' <para>
    ''' <b>不要在返回的控件里再套 ScrollViewer</b>：外层已经是 <c>MyScrollViewer</c>，
    ''' 它在 PreviewMouseWheel 里无条件 <c>e.Handled = True</c>，内层 ScrollViewer 收不到滚轮事件，套了反而滚不动。
    ''' 滚动交给外层。
    ''' </para>
    ''' </summary>
    ''' <param name="V">当前编辑的版本。只读，不会被修改。</param>
    ''' <param name="Fields">由本方法填充的取值委托表，交给 <see cref="ApplyConfig"/> 回读。</param>
    Public Function BuildConfigPanel(V As ServerVersion, Fields As Dictionary(Of String, Func(Of Object))) As FrameworkElement
        If V Is Nothing Then Throw New ArgumentNullException(NameOf(V))
        If Fields Is Nothing Then Throw New ArgumentNullException(NameOf(Fields))
        Fields.Clear()

        Dim Root As New StackPanel With {.Margin = New Thickness(0, 0, 0, 4)}

        '—— 基础 ——
        Dim GBase = AddGroup(Root, "基础", True)
        Fields("Port") = CfgNumber(GBase, "端口", V.Port, 1024, 65535,
            "玩家连接用的端口。默认 25565，被占用时换一个（如 25566）。")
        Fields("MemoryMB") = CfgNumber(GBase, "内存 (MB)", V.MemoryMB, 512, 65536,
            "分配给服务端的内存。1.18+ 建议至少 2048；模组服按模组数量往上加。")
        Fields("Motd") = CfgText(GBase, "服务器标语", V.Motd,
            "服务器列表里显示的名字，支持颜色代码（如 §a绿色）。留空则用版本名。")

        '—— 玩法 ——
        Dim GPlay = AddGroup(Root, "玩法", False)
        Fields("GameMode") = CfgChoice(GPlay, "默认模式", V.GameMode,
            {"survival", "creative", "adventure", "spectator"},
            {"生存", "创造", "冒险", "旁观"})
        Fields("Difficulty") = CfgChoice(GPlay, "难度", V.Difficulty,
            {"peaceful", "easy", "normal", "hard"},
            {"和平", "简单", "普通", "困难"})
        Fields("MaxPlayers") = CfgNumber(GPlay, "最大玩家数", V.MaxPlayers, 1, 1000, "")
        Fields("Pvp") = CfgToggle(GPlay, "允许 PVP", V.Pvp, "玩家之间可以互相伤害。")
        Fields("Hardcore") = CfgToggle(GPlay, "极限模式", V.Hardcore, "死亡后不能复活，谨慎开启。")
        Fields("AllowFlight") = CfgToggle(GPlay, "允许飞行", V.AllowFlight,
            "关闭可以防飞行外挂，但会让创造模式玩家掉下来。")
        Fields("ForceGameMode") = CfgToggle(GPlay, "强制默认模式", V.ForceGameMode,
            "开启后玩家每次进服都会被切成上面的默认模式。")
        Fields("SpawnMonsters") = CfgToggle(GPlay, "生成怪物", V.SpawnMonsters, "")
        Fields("SpawnAnimals") = CfgToggle(GPlay, "生成动物", V.SpawnAnimals, "")

        '—— 世界 ——
        Dim GWorld = AddGroup(Root, "世界", False)
        Fields("LevelName") = CfgText(GWorld, "世界文件夹", V.LevelName, "存档所在目录名，默认 world。")
        Fields("LevelSeed") = CfgText(GWorld, "世界种子", V.LevelSeed, "留空表示随机生成。")
        Fields("LevelType") = CfgChoice(GWorld, "世界类型", V.LevelType,
            {"minecraft:normal", "minecraft:flat", "minecraft:large_biomes", "minecraft:amplified"},
            {"默认", "超平坦", "巨型生物群系", "放大化"})
        Fields("ViewDistance") = CfgNumber(GWorld, "视距 (区块)", V.ViewDistance, 3, 32,
            "越大看得越远，但更吃内存和带宽。")
        Fields("SimulationDistance") = CfgNumber(GWorld, "模拟距离 (区块)", V.SimulationDistance, 3, 32,
            "多大范围内的实体和方块会真正参与运算。低于视距能明显省 CPU。")
        Fields("AllowNether") = CfgToggle(GWorld, "允许下界", V.AllowNether, "")
        Fields("SpawnProtection") = CfgNumber(GWorld, "出生点保护 (格)", V.SpawnProtection, 0, 1000,
            "非 OP 玩家在出生点周围不能破坏方块。0 表示关闭。")
        Fields("MaxWorldSize") = CfgNumber(GWorld, "世界半径上限 (格)", V.MaxWorldSize, 1, 29999984,
            "世界边界的最大半径，默认 29999984。改小可以限制玩家跑图范围。")

        '—— 联机与安全 ——
        Dim GNet = AddGroup(Root, "联机与安全", False)
        Fields("OnlineMode") = CfgToggle(GNet, "正版验证", V.OnlineMode,
            "开启：只有正版账号能进。关闭：离线账号也能进，但无法防冒名。")
        Fields("WhiteList") = CfgToggle(GNet, "启用白名单", V.WhiteList,
            "只有 whitelist.json 里的玩家能进。名单在下面的<数据>组里管理。")
        Fields("EnforceWhitelist") = CfgToggle(GNet, "强制白名单", V.EnforceWhitelist,
            "白名单外的在线玩家会被立即踢出。")
        Fields("EnableStatus") = CfgToggle(GNet, "显示服务器状态", V.EnableStatus,
            "在服务器列表里显示在线人数与 motd。关掉后列表里会显示离线。")
        Fields("PreventProxyConnections") = CfgToggle(GNet, "拒绝代理连接", V.PreventProxyConnections,
            "拒绝通过 VPN / 代理连进来的玩家，用于防封禁绕过。")
        Fields("EnableQuery") = CfgToggle(GNet, "启用 Query 协议", V.EnableQuery,
            "供第三方工具（查询网站、监控面板）读取在线列表。")
        Fields("QueryPort") = CfgNumber(GNet, "Query 端口", V.QueryPort, 1, 65535,
            "默认与服务端端口相同。开着 Query 时必须和服务端端口错开。")

        '—— RCON ——
        Dim GRcon = AddGroup(Root, "RCON 远程控制", False)
        Fields("EnableRcon") = CfgToggle(GRcon, "启用 RCON", V.EnableRcon,
            "允许外部程序通过 TCP 远程执行控制台命令。必须同时设置密码。")
        Fields("RconPort") = CfgNumber(GRcon, "RCON 端口", V.RconPort, 1, 65535,
            "默认 25575，不能和服务端端口相同。")
        Fields("RconPassword") = CfgText(GRcon, "RCON 密码", V.RconPassword,
            "开着 RCON 却没密码，服务端会拒绝启动。密码以明文存在 server.properties 里。")

        '—— 高级 ——
        Dim GAdv = AddGroup(Root, "高级", False)
        Fields("EnableCommandBlock") = CfgToggle(GAdv, "允许命令方块", V.EnableCommandBlock,
            "关闭后命令方块无法激活。")
        Fields("OpPermissionLevel") = CfgNumber(GAdv, "OP 权限等级", V.OpPermissionLevel, 1, 4,
            "1 最低，4 等同服主。默认 4。")
        Fields("FunctionPermissionLevel") = CfgNumber(GAdv, "函数权限等级", V.FunctionPermissionLevel, 1, 4,
            "数据包里的函数能用的最高权限，默认 2。")
        Fields("PlayerIdleTimeout") = CfgNumber(GAdv, "挂机踢出 (分钟)", V.PlayerIdleTimeout, 0, 1440,
            "多久不动就踢出，0 表示不踢。")
        Fields("NetworkCompressionThreshold") = CfgNumber(GAdv, "压缩阈值 (字节)", V.NetworkCompressionThreshold, -1, 65536,
            "小于该大小的数据包不压缩。-1 关闭压缩，0 全压缩，默认 256。")
        Fields("RateLimit") = CfgNumber(GAdv, "单玩家包速率上限", V.RateLimit, 0, 10000,
            "每秒最多多少个数据包，0 表示不限。")
        Fields("EntityBroadcastRangePercentage") = CfgNumber(GAdv, "实体广播范围 (%)", V.EntityBroadcastRangePercentage, 10, 1000,
            "实体位置同步给多远的玩家，100 是默认。调低能省带宽。")
        Fields("UseNativeTransport") = CfgToggle(GAdv, "使用原生传输", V.UseNativeTransport,
            "Linux 上用 epoll。Windows 上没有效果，保持默认即可。")
        Fields("SyncChunkWrites") = CfgToggle(GAdv, "区块同步落盘", V.SyncChunkWrites,
            "每次写区块都强制刷盘。断电更安全，但更慢。")

        '—— 数据 ——
        Dim GData = AddGroup(Root, "数据（备份 / 恢复 / 文件补全）", False)
        BuildDataGroup(GData, V)

        Return Root
    End Function

    ''' <summary>「数据」组：备份、恢复、文件补全三个按钮 + 说明。</summary>
    Private Sub BuildDataGroup(Parent As StackPanel, V As ServerVersion)
        Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 4, 0, 8)}
        Dim BtnBackup As New MyButton With {
            .Text = "立即备份", .Height = 30, .MinWidth = 96, .ColorType = MyButton.ColorState.Highlight
        }
        Dim BtnRestore As New MyButton With {
            .Text = "恢复备份", .Height = 30, .MinWidth = 96, .Margin = New Thickness(8, 0, 0, 0)
        }
        Dim BtnRepair As New MyButton With {
            .Text = "补全文件", .Height = 30, .MinWidth = 96, .Margin = New Thickness(8, 0, 0, 0)
        }
        Row.Children.Add(BtnBackup)
        Row.Children.Add(BtnRestore)
        Row.Children.Add(BtnRepair)
        Parent.Children.Add(Row)

        Dim Tip As New TextBlock With {
            .FontSize = 11, .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 0, 0, 4),
            .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
        }
        Tip.Text = "备份会把世界、server.properties、白名单、ops、config 与 mods 打成一个 zip，" & vbCrLf &
                   "存到 PCL\Servers\.backups\ 下对应版本的目录里。恢复前会自动再备份一次当前状态，" & vbCrLf &
                   "所以选错了备份也能回到恢复之前。"
        Parent.Children.Add(Tip)

        Dim WlRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 8, 0, 4)}
        Dim BtnWlAdd As New MyButton With {.Text = "加白名单", .Height = 30, .MinWidth = 96}
        Dim BtnWlDel As New MyButton With {.Text = "移出白名单", .Height = 30, .MinWidth = 96, .Margin = New Thickness(8, 0, 0, 0)}
        WlRow.Children.Add(BtnWlAdd)
        WlRow.Children.Add(BtnWlDel)
        Parent.Children.Add(WlRow)

        Dim WlTip As New TextBlock With {
            .FontSize = 11, .TextWrapping = TextWrapping.Wrap,
            .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
        }
        WlTip.Text = "服务端在跑的时候走控制台命令，改完立即生效；没在跑就直接写 whitelist.json。" & vbCrLf &
                     "离线 UUID 按原版算法生成，关掉正版验证的服务器能正确认人。"
        Parent.Children.Add(WlTip)

        AddHandler BtnBackup.Click, Sub()
                                        Dim P = BackupServer(V, "手动备份")
                                        If P Is Nothing Then
                                            Hint("备份失败，详见日志", HintType.Red)
                                        Else
                                            Hint($"已备份：{BackupDisplayName(P)}", HintType.Green)
                                        End If
                                    End Sub

        AddHandler BtnRestore.Click, Sub()
                                         Dim All = ListBackups(V)
                                         If All.Count = 0 Then
                                             Hint("这个服务端还没有任何备份", HintType.Blue)
                                             Return
                                         End If
                                         Dim Radios As New List(Of IMyRadio)
                                         For Each P In All
                                             Radios.Add(New MyRadioBox With {.Text = BackupDisplayName(P)})
                                         Next
                                         Dim Picked = MyMsgBoxSelect(Radios,
                                             $"选择要恢复的备份（共 {All.Count} 个）", "恢复", "取消", IsWarn:=True)
                                         If Picked Is Nothing Then Return
                                         Dim Idx = Picked.Value
                                         If Idx < 0 OrElse Idx >= All.Count Then Return
                                         Dim ErrText = RestoreServer(V, All(Idx))
                                         If ErrText.Length = 0 Then
                                             Hint("备份已恢复", HintType.Green)
                                         Else
                                             Hint(ErrText, HintType.Red)
                                         End If
                                     End Sub

        AddHandler BtnRepair.Click, Sub()
                                        Dim Fixed = RepairServerFiles(V)
                                        If Fixed.Count = 0 Then
                                            Hint("服务端文件齐全，没有需要补的", HintType.Green)
                                        Else
                                            Hint($"已补全 {Fixed.Count} 个文件：{String.Join(", ", Fixed)}", HintType.Green)
                                        End If
                                    End Sub

        AddHandler BtnWlAdd.Click, Sub()
                                       Dim Name = MyMsgBoxInput("加入白名单", "玩家名", "",
                                           HintText:="填正版 ID 或离线模式下用的名字")
                                       If String.IsNullOrWhiteSpace(Name) Then Return
                                       If AddToWhitelist(V, Name) Then
                                           Hint($"已把 {Name.Trim()} 加入白名单", HintType.Green)
                                       Else
                                           Hint("加入白名单失败，详见日志", HintType.Red)
                                       End If
                                   End Sub

        AddHandler BtnWlDel.Click, Sub()
                                       Dim Current = ReadWhitelist(V)
                                       If Current.Count = 0 Then
                                           Hint("白名单是空的", HintType.Blue)
                                           Return
                                       End If
                                       Dim Radios As New List(Of IMyRadio)
                                       For Each N In Current
                                           Radios.Add(New MyRadioBox With {.Text = N})
                                       Next
                                       Dim Picked = MyMsgBoxSelect(Radios, "选择要移出白名单的玩家", "移除", "取消", IsWarn:=True)
                                       If Picked Is Nothing Then Return
                                       Dim Idx = Picked.Value
                                       If Idx < 0 OrElse Idx >= Current.Count Then Return
                                       If RemoveFromWhitelist(V, Current(Idx)) Then
                                           Hint($"已把 {Current(Idx)} 移出白名单", HintType.Green)
                                       Else
                                           Hint("移除失败，详见日志", HintType.Red)
                                       End If
                                   End Sub
    End Sub

    ''' <summary>
    ''' 把面板里收集到的值写回 <paramref name="V"/> 并落盘，返回错误信息（空串表示成功）。
    ''' <para>
    ''' 顺序是「先赋值 → 校验 → 再落盘」：校验不过时内存里的对象已经被改了，
    ''' 但因为没写文件、也没写 server.properties，重启就恢复原样，不会留下半套配置。
    ''' </para>
    ''' </summary>
    Public Function ApplyConfig(V As ServerVersion, Fields As Dictionary(Of String, Func(Of Object))) As String
        Try
            If V Is Nothing Then Return "没有指定服务端版本"
            If Fields Is Nothing OrElse Fields.Count = 0 Then Return "配置面板没有生成任何字段"

            V.Port = TakeInt(Fields, "Port", V.Port)
            V.MemoryMB = TakeInt(Fields, "MemoryMB", V.MemoryMB)
            V.Motd = TakeStr(Fields, "Motd", V.Motd)

            V.GameMode = TakeStr(Fields, "GameMode", V.GameMode)
            V.Difficulty = TakeStr(Fields, "Difficulty", V.Difficulty)
            V.MaxPlayers = TakeInt(Fields, "MaxPlayers", V.MaxPlayers)
            V.Pvp = TakeBool(Fields, "Pvp", V.Pvp)
            V.Hardcore = TakeBool(Fields, "Hardcore", V.Hardcore)
            V.AllowFlight = TakeBool(Fields, "AllowFlight", V.AllowFlight)
            V.ForceGameMode = TakeBool(Fields, "ForceGameMode", V.ForceGameMode)
            V.SpawnMonsters = TakeBool(Fields, "SpawnMonsters", V.SpawnMonsters)
            V.SpawnAnimals = TakeBool(Fields, "SpawnAnimals", V.SpawnAnimals)

            V.LevelName = TakeStr(Fields, "LevelName", V.LevelName)
            V.LevelSeed = TakeStr(Fields, "LevelSeed", V.LevelSeed)
            V.LevelType = TakeStr(Fields, "LevelType", V.LevelType)
            V.ViewDistance = TakeInt(Fields, "ViewDistance", V.ViewDistance)
            V.SimulationDistance = TakeInt(Fields, "SimulationDistance", V.SimulationDistance)
            V.AllowNether = TakeBool(Fields, "AllowNether", V.AllowNether)
            V.SpawnProtection = TakeInt(Fields, "SpawnProtection", V.SpawnProtection)
            V.MaxWorldSize = TakeInt(Fields, "MaxWorldSize", V.MaxWorldSize)

            V.OnlineMode = TakeBool(Fields, "OnlineMode", V.OnlineMode)
            V.WhiteList = TakeBool(Fields, "WhiteList", V.WhiteList)
            V.EnforceWhitelist = TakeBool(Fields, "EnforceWhitelist", V.EnforceWhitelist)
            V.EnableStatus = TakeBool(Fields, "EnableStatus", V.EnableStatus)
            V.PreventProxyConnections = TakeBool(Fields, "PreventProxyConnections", V.PreventProxyConnections)
            V.EnableQuery = TakeBool(Fields, "EnableQuery", V.EnableQuery)
            V.QueryPort = TakeInt(Fields, "QueryPort", V.QueryPort)

            V.EnableRcon = TakeBool(Fields, "EnableRcon", V.EnableRcon)
            V.RconPort = TakeInt(Fields, "RconPort", V.RconPort)
            V.RconPassword = TakeStr(Fields, "RconPassword", V.RconPassword)

            V.EnableCommandBlock = TakeBool(Fields, "EnableCommandBlock", V.EnableCommandBlock)
            V.OpPermissionLevel = TakeInt(Fields, "OpPermissionLevel", V.OpPermissionLevel)
            V.FunctionPermissionLevel = TakeInt(Fields, "FunctionPermissionLevel", V.FunctionPermissionLevel)
            V.PlayerIdleTimeout = TakeInt(Fields, "PlayerIdleTimeout", V.PlayerIdleTimeout)
            V.NetworkCompressionThreshold = TakeInt(Fields, "NetworkCompressionThreshold", V.NetworkCompressionThreshold)
            V.RateLimit = TakeInt(Fields, "RateLimit", V.RateLimit)
            V.EntityBroadcastRangePercentage = TakeInt(Fields, "EntityBroadcastRangePercentage", V.EntityBroadcastRangePercentage)
            V.UseNativeTransport = TakeBool(Fields, "UseNativeTransport", V.UseNativeTransport)
            V.SyncChunkWrites = TakeBool(Fields, "SyncChunkWrites", V.SyncChunkWrites)

            Dim ErrText = ValidateConfig(V)
            If Not String.IsNullOrEmpty(ErrText) Then Return ErrText

            ModServer.SaveConfig(V)
            ModServer.WriteProperties(V)
            Return ""
        Catch ex As Exception
            Logger.Error(ex, "保存服务端配置失败")
            Return $"保存失败：{ex.Message}"
        End Try
    End Function

    '—— Fields 取值：缺键或类型不对时一律退回原值，绝不抛异常 ——

    Private Function TakeStr(Fields As Dictionary(Of String, Func(Of Object)), Key As String, Fallback As String) As String
        Try
            Dim F As Func(Of Object) = Nothing
            If Fields.TryGetValue(Key, F) AndAlso F IsNot Nothing Then
                Dim R = F()
                If R IsNot Nothing Then Return CStr(R)
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"读取配置项失败：{Key}")
        End Try
        Return Fallback
    End Function

    Private Function TakeInt(Fields As Dictionary(Of String, Func(Of Object)), Key As String, Fallback As Integer) As Integer
        Try
            Dim F As Func(Of Object) = Nothing
            If Fields.TryGetValue(Key, F) AndAlso F IsNot Nothing Then
                Dim R = F()
                If R IsNot Nothing Then
                    Dim N As Integer = 0
                    If Integer.TryParse(CStr(R), N) Then Return N
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"读取配置项失败：{Key}")
        End Try
        Return Fallback
    End Function

    Private Function TakeBool(Fields As Dictionary(Of String, Func(Of Object)), Key As String, Fallback As Boolean) As Boolean
        Try
            Dim F As Func(Of Object) = Nothing
            If Fields.TryGetValue(Key, F) AndAlso F IsNot Nothing Then
                Dim R = F()
                If TypeOf R Is Boolean Then Return CBool(R)
                If R IsNot Nothing Then
                    Dim B As Boolean = False
                    If Boolean.TryParse(CStr(R), B) Then Return B
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"读取配置项失败：{Key}")
        End Try
        Return Fallback
    End Function

    '==== 面板小部件 ====

    ''' <summary>
    ''' 加一个可折叠分组，返回组内用来放控件的 StackPanel。
    ''' <para>箭头旋转 + 内容淡入，都是 160ms 的弱缓动——折叠是高频小操作，动画一长就烦人。</para>
    ''' </summary>
    Private Function AddGroup(Parent As StackPanel, Title As String, Expanded As Boolean) As StackPanel
        Dim Wrap As New StackPanel With {.Margin = New Thickness(0, 1, 0, 1)}

        Dim Header As New Grid With {
            .Background = Brushes.Transparent,
            .Cursor = Cursors.Hand,
            .Height = 30,
            .Margin = New Thickness(0, 2, 0, 0)
        }
        Header.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Header.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim Lab As New TextBlock With {
            .Text = Title, .FontSize = 12, .FontWeight = FontWeights.SemiBold,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
        Grid.SetColumn(Lab, 0)
        Header.Children.Add(Lab)

        Dim Angle As Double = If(Expanded, 90.0, 0.0)
        Dim Arrow As New Shapes.Path With {
            .Data = New GeometryConverter().ConvertFromString("M0,0 L6,6 L0,12"),
            .Width = 6, .Height = 12, .Stretch = Stretch.Uniform,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(0, 0, 6, 0),
            .RenderTransformOrigin = New Point(0.5, 0.5),
            .RenderTransform = New RotateTransform(Angle, 3, 6)
        }
        Arrow.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush4")
        Grid.SetColumn(Arrow, 1)
        Header.Children.Add(Arrow)

        Dim Content As New StackPanel With {.Margin = New Thickness(0, 2, 0, 8)}
        Content.Visibility = If(Expanded, Visibility.Visible, Visibility.Collapsed)

        Wrap.Children.Add(Header)
        Wrap.Children.Add(Content)
        Parent.Children.Add(Wrap)

        Dim AniName = "ServerCfgGroup " & Wrap.GetHashCode().ToString()
        AddHandler Header.MouseLeftButtonUp, Sub()
                                                 Dim Show As Boolean = Content.Visibility <> Visibility.Visible
                                                 Content.Visibility = If(Show, Visibility.Visible, Visibility.Collapsed)
                                                 Dim Cur = TryCast(Arrow.RenderTransform, RotateTransform)
                                                 Dim From As Double = If(Cur Is Nothing, 0.0, Cur.Angle)
                                                 Dim [To] As Double = If(Show, 90.0, 0.0)
                                                 AniStart(AaRotateTransform(Arrow, [To] - From, 160, ,
                                                     New AniEaseOutFluent(AniEasePower.Weak)), AniName)
                                                 If Show Then
                                                     Content.Opacity = 0
                                                     AniStart(AaOpacity(Content, 1, 160, ,
                                                         New AniEaseOutFluent(AniEasePower.Weak)), AniName & " fade")
                                                 End If
                                             End Sub

        Return Content
    End Function

    ''' <summary>一行：左边标题（120px 固定宽），右边输入控件。</summary>
    Private Function CfgRow(Label As String) As Grid
        Dim Row As New Grid With {.Margin = New Thickness(0, 0, 0, 6)}
        Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(120)})
        Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})

        Dim Lab As New TextBlock With {
            .Text = Label, .VerticalAlignment = VerticalAlignment.Center, .FontSize = 12
        }
        Lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Grid.SetColumn(Lab, 0)
        Row.Children.Add(Lab)
        Return Row
    End Function

    ''' <summary>说明文字：11px 灰字，缩进对齐到输入框左边。</summary>
    Private Sub CfgTip(Parent As StackPanel, Tip As String, Indent As Double)
        If String.IsNullOrWhiteSpace(Tip) Then Return
        Dim T As New TextBlock With {
            .Text = Tip, .FontSize = 11, .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(Indent, 0, 0, 8)
        }
        T.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        T.Opacity = 0.55
        Parent.Children.Add(T)
    End Sub

    Private Function CfgNumber(Parent As StackPanel, Label As String, Init As Integer,
                               Min As Integer, Max As Integer, Tip As String) As Func(Of Object)
        Dim Row = CfgRow(Label)
        Dim Box As New MyTextBox With {
            .Height = 30, .Text = Init.ToString(), .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(Box, 1)
        Row.Children.Add(Box)
        Parent.Children.Add(Row)
        CfgTip(Parent, Tip, 120)

        Return Function()
                   Dim N As Integer = Init
                   If Integer.TryParse(Box.Text, N) Then
                       Return Math.Max(Min, Math.Min(Max, N))
                   End If
                   Return Math.Max(Min, Math.Min(Max, Init))
               End Function
    End Function

    Private Function CfgText(Parent As StackPanel, Label As String, Init As String,
                             Tip As String) As Func(Of Object)
        Dim Row = CfgRow(Label)
        Dim Box As New MyTextBox With {
            .Height = 30, .Text = If(Init, ""), .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(Box, 1)
        Row.Children.Add(Box)
        Parent.Children.Add(Row)
        CfgTip(Parent, Tip, 120)

        Return Function()
                   Return If(Box.Text, "")
               End Function
    End Function

    Private Function CfgChoice(Parent As StackPanel, Label As String, Init As String,
                               Values As String(), Displays As String()) As Func(Of Object)
        Dim Row = CfgRow(Label)
        Dim Combo As New MyComboBox With {.Height = 30, .VerticalAlignment = VerticalAlignment.Center}

        Dim SelIdx As Integer = 0
        For i = 0 To Values.Length - 1
            Dim Item As New MyComboBoxItem With {.Content = Displays(i)}
            Item.Tag = Values(i)
            Combo.Items.Add(Item)
            If Values(i) = Init Then SelIdx = i
        Next
        Combo.SelectedIndex = SelIdx
        Grid.SetColumn(Combo, 1)
        Row.Children.Add(Combo)
        Parent.Children.Add(Row)

        Return Function()
                   Dim It = TryCast(Combo.SelectedItem, MyComboBoxItem)
                   If It Is Nothing OrElse It.Tag Is Nothing Then Return Init
                   Return It.Tag.ToString()
               End Function
    End Function

    Private Function CfgToggle(Parent As StackPanel, Label As String, Init As Boolean,
                               Tip As String) As Func(Of Object)
        Dim Box As New MyCheckBox With {
            .Text = Label, .Checked = Init, .Margin = New Thickness(0, 0, 0, 2)
        }
        Parent.Children.Add(Box)
        CfgTip(Parent, Tip, 26)

        Return Function()
                   Return Box.Checked
               End Function
    End Function

End Module
