''' <summary>
''' SLATCL 插件系统（AstrBot 风格）。
''' 
''' 插件格式：一个文件夹，里面必须有：
'''   plugin.json  — 元数据 {"name","version","author","description","entry","permissions"}
'''   main.jar     — Java 可执行 jar（入口 = plugin.json 的 entry 字段）
'''   README.md    — 说明文档（可选）
'''   plugin.xaml  — 可选 UI 描述（后续扩展）
'''
''' 通信协议：启动器 spawn java -jar main.jar，通过 stdin/stdout 传 JSON 行。
''' 插件可以发送：
'''   {"action":"log","message":"..."}                    — 写日志
'''   {"action":"toast","message":"...","type":"info"}   — 弹提示
'''   {"action":"register_page","title":"...","content_html":"..."} — 注册新页面
'''   {"action":"add_button","target":"server","text":"...","id":"..."} — 添加功能入口
'''
''' 启动器发送给插件：
'''   {"event":"loaded","plugin_dir":"..."}              — 加载完成
'''   {"event":"console_line","line":"..."}              — 控制台输出
'''
''' 安装方式：
'''   1. GitHub：下载 repo zip 并解压到 Plugins/{name}/
'''   2. 本地：复制文件夹到 Plugins/{name}/
'''
''' 权限：全部开放，但所有操作记入审计日志，并在加载时向用户展示权限清单。
''' </summary>
Public Class SlatPlugin
    Public Name As String = ""
    Public Version As String = ""
    Public Author As String = ""
    Public Description As String = ""
    Public Entry As String = "main.jar"
    Public Permissions As New List(Of String)
    Public Dir As String = ""
    Public Proc As Process = Nothing
    Public IsRunning As Boolean = False
End Class

Public Module ModPluginHost

    ''' <summary>所有已加载的插件。</summary>
    Public Plugins As New List(Of SlatPlugin)

    ''' <summary>插件根目录（exe 旁的 Plugins/）。</summary>
    Public ReadOnly Property PluginDir As String
        Get
            Return IO.Path.Combine(Paths.Base, "Plugins")
        End Get
    End Property

    ''' <summary>扫描并加载所有插件。在启动时调用一次。</summary>
    Public Sub LoadAll()
        Try
            If Not IO.Directory.Exists(PluginDir) Then IO.Directory.CreateDirectory(PluginDir)
            Plugins.Clear()
            For Each D In IO.Directory.GetDirectories(PluginDir)
                Try
                    Dim MetaPath = IO.Path.Combine(D, "plugin.json")
                    If Not IO.File.Exists(MetaPath) Then Continue For
                    Dim Meta = Newtonsoft.Json.Linq.JObject.Parse(IO.File.ReadAllText(MetaPath))
                    Dim P As New SlatPlugin With {
                        .Name = Meta("name")?.ToString(),
                        .Version = Meta("version")?.ToString(),
                        .Author = Meta("author")?.ToString(),
                        .Description = Meta("description")?.ToString(),
                        .Entry = If(Meta("entry")?.ToString(), "main.jar"),
                        .Dir = D}
                    If Meta("permissions") IsNot Nothing Then
                        For Each Perm In Meta("permissions")
                            P.Permissions.Add(Perm.ToString())
                        Next
                    End If
                    If String.IsNullOrWhiteSpace(P.Name) OrElse Not IO.File.Exists(IO.Path.Combine(D, P.Entry)) Then Continue For
                    Plugins.Add(P)
                    Logger.Info($"插件已发现：{P.Name} v{P.Version} ({P.Dir})")
                Catch ex As Exception
                    Logger.Warn(ex, $"解析插件元数据失败：{D}")
                End Try
            Next
            Logger.Info($"插件扫描完成：{Plugins.Count} 个")

            '完整性校验：指纹变了说明 plugin.json / jar 被改过，
            '作废该插件已有的授权记录，强制用户重新确认权限
            Dim Tampered = PluginProtection.VerifyAll()
            For Each T In Tampered
                Try
                    Dim PermFile = IO.Path.Combine(PluginDir, ".permissions", T.PluginName & ".json")
                    If IO.File.Exists(PermFile) Then
                        PluginProtection.WithWritable(PermFile, Sub() IO.File.Delete(PermFile))
                    End If
                    Logger.Warn($"[保护] {T.PluginName} 被篡改（{T.Reason}），已作废其授权记录")
                Catch ex As Exception
                    Logger.Warn(ex, $"作废 {T.PluginName} 授权记录失败")
                End Try
            Next
            If Tampered.Count > 0 Then
                Dim Names = String.Join("、", Tampered.Select(Function(x) x.PluginName))
                RunInUi(Sub() Hint($"以下插件文件被修改过，权限已重置：{Names}", HintType.Blue))
            End If

            '加固权限存储（只读 + 隐藏）
            PluginProtection.HardenStorage()
        Catch ex As Exception
            Logger.Error(ex, "插件扫描失败")
        End Try
    End Sub

    ''' <summary>启动一个插件的 Java 进程并建立 IPC。</summary>
    Public Sub Start(P As SlatPlugin)
        If P.IsRunning Then Return
        Try
            Dim JarPath = IO.Path.Combine(P.Dir, P.Entry)
            If Not IO.File.Exists(JarPath) Then
                Logger.Warn($"插件入口不存在：{JarPath}")
                Return
            End If
            Dim Psi As New ProcessStartInfo With {
                .FileName = "java",
                .Arguments = $"-jar ""{JarPath}""",
                .WorkingDirectory = P.Dir,
                .UseShellExecute = False, .CreateNoWindow = True,
                .RedirectStandardInput = True, .RedirectStandardOutput = True, .RedirectStandardError = True}
            P.Proc = Process.Start(Psi)
            P.IsRunning = True

            '发 loaded 事件
            SendToPlugin(P, New With {.event = "loaded", .plugin_dir = P.Dir})

            '读 stdout（JSON 行）→ 处理 UI 注入命令
            RunInNewThread(
                Sub()
                    Try
                        While P.IsRunning AndAlso Not P.Proc.HasExited
                            Dim Line = P.Proc.StandardOutput.ReadLine()
                            If Line Is Nothing Then Exit While
                            If String.IsNullOrWhiteSpace(Line) Then Continue While
                            HandlePluginMessage(P, Line)
                        End While
                    Catch
                    End Try
                    P.IsRunning = False
                End Sub, $"Plugin-{P.Name}")

            'stderr → 日志
            RunInNewThread(
                Sub()
                    Try
                        While P.IsRunning AndAlso Not P.Proc.HasExited
                            Dim Line = P.Proc.StandardError.ReadLine()
                            If Line IsNot Nothing AndAlso Line.Length > 0 Then
                                Logger.Info($"[{P.Name}] {Line}")
                            End If
                        End While
                    Catch
                    End Try
                End Sub, $"Plugin-{P.Name}-Err")

            '权限检查：首次启动展示全部权限清单，一次性授权或逐项选择
            If Not PluginPermission.HasBeenGranted(P.Name) Then
                RunInUiWait(Sub()
                    PluginPermission.ShowFirstRunDialog(P.Name, P.Permissions)
                End Sub)
            End If

            '审计日志
            Dim G2 = PluginPermission.GetGrants(P.Name)
            Logger.Info($"插件已启动：{P.Name} v{P.Version} | 已授权：{String.Join(", ", G2.Granted)} | 已拒绝：{String.Join(", ", G2.Denied)}")
        Catch ex As Exception
            Logger.Error(ex, $"启动插件失败：{P.Name}")
            P.IsRunning = False
        End Try
    End Sub

    ''' <summary>停止插件进程。</summary>
    Public Sub StopPlugin(P As SlatPlugin)
        If Not P.IsRunning Then Return
        Try
            P.Proc.StandardInput.WriteLine("""{""event"":""shutdown""}")
            P.Proc.StandardInput.Flush()
            If Not P.Proc.WaitForExit(5000) Then
                P.Proc.Kill()
            End If
        Catch
        End Try
        P.IsRunning = False
        Logger.Info($"插件已停止：{P.Name}")
    End Sub

    ''' <summary>发送 JSON 到插件 stdin。</summary>
    Public Sub SendToPlugin(P As SlatPlugin, Data As Object)
        Try
            If P.Proc IsNot Nothing AndAlso Not P.Proc.HasExited Then
                Dim Json = Newtonsoft.Json.JsonConvert.SerializeObject(Data)
                P.Proc.StandardInput.WriteLine(Json)
                P.Proc.StandardInput.Flush()
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"发送到插件失败：{P.Name}")
        End Try
    End Sub

    ''' <summary>处理插件发来的消息（UI 注入、日志、提示等）。</summary>
    Public Sub HandlePluginMessage(P As SlatPlugin, Line As String)
        Try
            Dim Msg = Newtonsoft.Json.Linq.JObject.Parse(Line)
            Dim Action = Msg("action")?.ToString()
            '路径保护优先于权限：即使有 filesystem 权限，也不能碰受保护区域
            Dim TargetPath = Msg("path")?.ToString()
            If Not String.IsNullOrWhiteSpace(TargetPath) Then
                If PluginProtection.IsProtectedPath(P.Name, TargetPath) Then
                    RunInUi(Sub() Hint($"插件 {P.Name} 试图访问受保护路径，已拒绝：{IO.Path.GetFileName(TargetPath)}", HintType.Red))
                    Logger.Warn($"[保护] 拦截 {P.Name} → {TargetPath}")
                    Return
                End If
            End If

            '运行时权限检查
            If Not PluginPermission.CheckRuntime(P.Name, Action, Line.Substring(0, Math.Min(100, Line.Length))) Then
                RunInUi(Sub() Hint($"插件 {P.Name} 尝试执行未授权的操作（{Action}），已拦截", HintType.Blue))
                Return
            End If
            If Action = "log" Then
                Logger.Info($"[{P.Name}] {Msg("message")?.ToString()}")
            ElseIf Action = "toast" Then
                RunInUi(Sub() Hint($"[{P.Name}] {Msg("message")?.ToString()}", HintType.Blue))
            ElseIf Action = "register_page" Then
                Dim Title = Msg("title")?.ToString()
                Dim ContentHtml = Msg("content_html")?.ToString()
                Logger.Info($"插件注册页面：{Title}")
                'TODO: 注入到 FormMain 的页面系统
            ElseIf Action = "add_button" Then
                Dim Target = Msg("target")?.ToString()
                Dim BtnText = Msg("text")?.ToString()
                Dim BtnId = Msg("id")?.ToString()
                Logger.Info($"插件添加按钮：{BtnText} → {Target}")
                'TODO: 注入到指定页面的功能区
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"处理插件消息失败：{Line.Substring(0, Math.Min(80, Line.Length))}")
        End Try
    End Sub

    ''' <summary>从 GitHub 安装插件（下载 repo zip → 解压到 Plugins/{name}/）。</summary>
    Public Function InstallFromGitHub(RepoUrl As String) As SlatPlugin
        Try
            'GitHub repo URL → zip 下载 URL
            Dim ZipUrl = RepoUrl.TrimEnd("/"c) & "/archive/refs/heads/main.zip"
            If RepoUrl.Contains("/tree/") Then
                '如果是 /tree/branch 格式，提取分支名
                Dim Parts = RepoUrl.Split("/"c)
                Dim Branch = Parts(Parts.Length - 1)
                Dim Base = RepoUrl.Substring(0, RepoUrl.IndexOf("/tree/"))
                ZipUrl = Base.TrimEnd("/"c) & "/archive/refs/heads/" & Branch & ".zip"
            End If

            Dim TempZip = IO.Path.Combine(IO.Path.GetTempPath(), "slatcl_plugin_" & Guid.NewGuid().ToString("N") & ".zip")
            Dim Req = CType(Net.WebRequest.Create(ZipUrl), Net.HttpWebRequest)
            Req.Timeout = 120000
            Req.UserAgent = "SLATCL/0.1"
            Using Resp = Req.GetResponse()
                Using S = Resp.GetResponseStream()
                    Using F As New IO.FileStream(TempZip, IO.FileMode.Create)
                        Dim Buf(65535) As Byte
                        While True
                            Dim N = S.Read(Buf, 0, Buf.Length)
                            If N <= 0 Then Exit While
                            F.Write(Buf, 0, N)
                        End While
                    End Using
                End Using
            End Using

            '解压到临时目录，找到 plugin.json 确定插件名
            Dim TempExtract = IO.Path.Combine(IO.Path.GetTempPath(), "slatcl_plugin_" & Guid.NewGuid().ToString("N"))
            '不用 Using：避免 VB 报「变量已关闭」
            Dim Zip As IO.Compression.ZipArchive = New IO.Compression.ZipArchive(IO.File.OpenRead(TempZip), IO.Compression.ZipArchiveMode.Read)
            Try
                For Each Entry In Zip.Entries
                    Dim EntryPath = Entry.FullName
                    Dim Slash = EntryPath.IndexOf("/"c)
                    If Slash > 0 Then EntryPath = EntryPath.Substring(Slash + 1)
                    If EntryPath = "" Then Continue For
                    Dim ExtractDest = IO.Path.Combine(TempExtract, EntryPath)
                    If Entry.FullName.EndsWith("/") Then
                        IO.Directory.CreateDirectory(ExtractDest)
                    Else
                        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(ExtractDest))
                        Entry.ExtractToFile(ExtractDest, True)
                    End If
                Next
            Finally
                Zip.Dispose()
            End Try

            '找 plugin.json
            Dim MetaPath = IO.Path.Combine(TempExtract, "plugin.json")
            If Not IO.File.Exists(MetaPath) Then
                For Each D In IO.Directory.GetDirectories(TempExtract)
                    If IO.File.Exists(IO.Path.Combine(D, "plugin.json")) Then
                        MetaPath = IO.Path.Combine(D, "plugin.json")
                        Exit For
                    End If
                Next
            End If
            If Not IO.File.Exists(MetaPath) Then
                Logger.Warn("GitHub 插件缺少 plugin.json")
                Return Nothing
            End If

            Dim Meta = Newtonsoft.Json.Linq.JObject.Parse(IO.File.ReadAllText(MetaPath))
            Dim Name = Meta("name")?.ToString()
            If String.IsNullOrWhiteSpace(Name) Then
                Logger.Warn("plugin.json 缺少 name 字段")
                Return Nothing
            End If

            '复制到 Plugins/{name}/
            Dim Dest = IO.Path.Combine(PluginDir, Name)
            If IO.Directory.Exists(Dest) Then IO.Directory.Delete(Dest, True)
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(Dest))
            If IO.Directory.Exists(MetaPath.Replace("\plugin.json", "")) Then
                CopyDir(MetaPath.Replace("\plugin.json", ""), Dest)
            End If

            '清理临时文件
            IO.File.Delete(TempZip)
            IO.Directory.Delete(TempExtract, True)

            '重新扫描并记录新指纹
            LoadAll()
            PluginProtection.SaveManifest()
            Return Plugins.FirstOrDefault(Function(x) x.Name = Name)
        Catch ex As Exception
            Logger.Error(ex, $"从 GitHub 安装插件失败：{RepoUrl}")
            Return Nothing
        End Try
    End Function

    ''' <summary>从本地文件夹安装插件。</summary>
    Public Function InstallFromLocal(SourceDir As String) As SlatPlugin
        Try
            Dim MetaPath = IO.Path.Combine(SourceDir, "plugin.json")
            If Not IO.File.Exists(MetaPath) Then
                Logger.Warn("本地文件夹缺少 plugin.json")
                Return Nothing
            End If
            Dim Meta = Newtonsoft.Json.Linq.JObject.Parse(IO.File.ReadAllText(MetaPath))
            Dim Name = Meta("name")?.ToString()
            If String.IsNullOrWhiteSpace(Name) Then Return Nothing

            Dim Dest = IO.Path.Combine(PluginDir, Name)
            If IO.Directory.Exists(Dest) Then IO.Directory.Delete(Dest, True)
            CopyDir(SourceDir, Dest)
            LoadAll()
            PluginProtection.SaveManifest()
            Return Plugins.FirstOrDefault(Function(x) x.Name = Name)
        Catch ex As Exception
            Logger.Error(ex, "本地安装插件失败")
            Return Nothing
        End Try
    End Function

    ''' <summary>卸载插件。</summary>
    Public Sub UninstallPlugin(Name As String)
        Dim P = Plugins.FirstOrDefault(Function(x) x.Name = Name)
        If P IsNot Nothing Then StopPlugin(P)
        Dim Dir = IO.Path.Combine(PluginDir, Name)
        If IO.Directory.Exists(Dir) Then IO.Directory.Delete(Dir, True)
        Plugins.RemoveAll(Function(x) x.Name = Name)
        Logger.Info($"插件已卸载：{Name}")
    End Sub

    Private Sub CopyDir(Src As String, Dst As String)
        IO.Directory.CreateDirectory(Dst)
        For Each F In IO.Directory.GetFiles(Src)
            IO.File.Copy(F, IO.Path.Combine(Dst, IO.Path.GetFileName(F)), True)
        Next
        For Each D In IO.Directory.GetDirectories(Src)
            CopyDir(D, IO.Path.Combine(Dst, IO.Path.GetFileName(D)))
        Next
    End Sub
End Module
