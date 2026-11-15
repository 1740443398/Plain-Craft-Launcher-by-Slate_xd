''' <summary>
''' 插件权限系统（安卓风格）：
''' 安装/首次启动时展示权限清单，用户逐项授权或拒绝。
''' 运行时拦截：插件发送的 JSON 命令按权限分类检查，未授权的操作被拦截并记审计日志。
'''
''' 权限分类：
'''   network      — 联网（发起 HTTP 请求）
'''   filesystem   — 读写文件（启动器目录之外的文件）
'''   config       — 修改配置（server.properties 或启动器设置）
'''   console      — 执行命令（向运行中的服务端发送控制台命令）
'''   ui           — 修改界面（添加按钮 / 页面 / 修改布局）
'''   backup       — 备份数据（访问备份文件）
'''
''' 存储：Plugins/.permissions/{plugin_name}.json
'''   {"granted":["network","ui"],"denied":["filesystem"],"first_run":"2026-11-14T..."}
''' </summary>
Public Class PluginPermission

    ''' <summary>权限定义：ID → 显示名 + 说明。</summary>
    Public Shared ReadOnly AllPermissions As New Dictionary(Of String, PermissionInfo) From {
        {"network", New PermissionInfo("联网", "插件可以发起网络请求（下载更新、查询 API 等）")},
        {"filesystem", New PermissionInfo("读写文件", "插件可以读取或修改启动器目录之外的文件")},
        {"config", New PermissionInfo("修改配置", "插件可以修改 server.properties 或启动器设置")},
        {"console", New PermissionInfo("执行命令", "插件可以向运行中的服务端发送控制台命令")},
        {"ui", New PermissionInfo("修改界面", "插件可以添加按钮、页面或修改启动器布局")},
        {"backup", New PermissionInfo("备份数据", "插件可以访问或修改备份文件")}
    }

    Public Class PermissionInfo
        Public Label As String
        Public Description As String
        Public Sub New(Label As String, Description As String)
            Me.Label = Label
            Me.Description = Description
        End Sub
    End Class

    ''' <summary>单个插件的授权状态。</summary>
    Public Class PluginGrants
        Public Name As String = ""
        Public Granted As New List(Of String)
        Public Denied As New List(Of String)
        Public FirstRun As String = ""
    End Class

    Private Shared ReadOnly PermDir As String =
        IO.Path.Combine(Paths.Base, "Plugins", ".permissions")

    ''' <summary>读取插件的授权状态。没有记录则返回空（首次运行）。</summary>
    Public Shared Function GetGrants(PluginName As String) As PluginGrants
        Try
            Dim Path = IO.Path.Combine(PermDir, PluginName & ".json")
            If Not IO.File.Exists(Path) Then Return New PluginGrants With {.Name = PluginName}
            Dim J = Newtonsoft.Json.Linq.JObject.Parse(IO.File.ReadAllText(Path))
            Dim G As New PluginGrants With {
                .Name = PluginName,
                .FirstRun = If(J("first_run")?.ToString(), "")}
            For Each Item In J("granted")
                G.Granted.Add(Item.ToString())
            Next
            For Each Item In J("denied")
                G.Denied.Add(Item.ToString())
            Next
            Return G
        Catch ex As Exception
            Logger.Warn(ex, $"读取插件权限失败：{PluginName}")
            Return New PluginGrants With {.Name = PluginName}
        End Try
    End Function

    ''' <summary>保存插件的授权状态。</summary>
    Public Shared Sub SaveGrants(G As PluginGrants)
        Try
            IO.Directory.CreateDirectory(PermDir)
            Dim J As New Newtonsoft.Json.Linq.JObject From {
                {"name", G.Name},
                {"first_run", G.FirstRun}}
            Dim GrantedArr As New Newtonsoft.Json.Linq.JArray
            For Each Item In G.Granted
                GrantedArr.Add(Item)
            Next
            J("granted") = GrantedArr
            Dim DeniedArr As New Newtonsoft.Json.Linq.JArray
            For Each Item In G.Denied
                DeniedArr.Add(Item)
            Next
            J("denied") = DeniedArr
            IO.File.WriteAllText(
                IO.Path.Combine(PermDir, G.Name & ".json"),
                J.ToString(Newtonsoft.Json.Formatting.Indented))
        Catch ex As Exception
            Logger.Error(ex, $"保存插件权限失败：{G.Name}")
        End Try
    End Sub

    ''' <summary>检查插件是否已被用户授权过（不需要再弹窗）。</summary>
    Public Shared Function HasBeenGranted(PluginName As String) As Boolean
        Dim G = GetGrants(PluginName)
        Return G.FirstRun <> ""
    End Function

    ''' <summary>检查某项权限是否已授权。</summary>
    Public Shared Function IsGranted(PluginName As String, Permission As String) As Boolean
        Dim G = GetGrants(PluginName)
        If G.Granted.Contains(Permission) Then Return True
        If G.Denied.Contains(Permission) Then Return False
        Return False
    End Function

    ''' <summary>运行时权限检查：未授权时弹对话框让用户决定，授予后无缝继续执行。</summary>
    Public Shared Function CheckRuntime(PluginName As String, Action As String, Detail As String) As Boolean
        Dim Perm = ActionToPermission(Action)
        If Perm = "" Then Return True

        Dim G = GetGrants(PluginName)
        If G.Granted.Contains(Perm) Then
            Logger.Info($"[审计] {PluginName} 执行 {Action}（{Perm}）：{Detail}")
            Return True
        End If

        '已拒绝：不再弹窗，直接拦截
        If G.Denied.Contains(Perm) Then
            Logger.Warn($"[审计] {PluginName} 的 {Perm} 权限已被用户拒绝，操作被拦截")
            Return False
        End If

        '未决定：弹对话框让用户选择，授予后无缝继续
        Dim Info = AllPermissions(Perm)
        Dim GrantedNow = False
        RunInUiWait(Sub()
            Dim Ok = MyMsgBox(
                "插件「" & PluginName & "」想要以下权限：" & vbCrLf & vbCrLf &
                Info.Label & vbCrLf & Info.Description & vbCrLf & vbCrLf &
                "具体操作：" & Detail & vbCrLf & vbCrLf &
                "授予后不会再询问。拒绝后相关功能将不可用（可以之后重新允许）。",
                PluginName & " · 权限请求", "授予并继续", "拒绝")
            GrantedNow = (Ok = 1)
        End Sub)

        '更新授权状态
        Dim G2 = GetGrants(PluginName)
        If GrantedNow Then
            If Not G2.Granted.Contains(Perm) Then G2.Granted.Add(Perm)
            G2.Denied.Remove(Perm)
            Logger.Info($"[审计] 用户授予 {PluginName} 的 {Perm} 权限，操作继续执行")
        Else
            If Not G2.Denied.Contains(Perm) Then G2.Denied.Add(Perm)
            Logger.Info($"[审计] 用户拒绝 {PluginName} 的 {Perm} 权限，操作被拦截")
        End If
        SaveGrants(G2)

        Return GrantedNow
    End Function

    ''' <summary>
    ''' 首次启动对话框：一次性展示插件声明的全部权限，
    ''' 用户可以「全部授予」或「逐项选择」。
    ''' </summary>
    Public Shared Sub ShowFirstRunDialog(PluginName As String, DeclaredPermissions As List(Of String))
        '过滤出有效的权限
        Dim ValidPerms As New List(Of String)
        For Each Perm In DeclaredPermissions
            If AllPermissions.ContainsKey(Perm) Then ValidPerms.Add(Perm)
        Next
        If ValidPerms.Count = 0 Then
            '没有声明任何权限，自动全部开放
            Dim AutoG As New PluginGrants With {
                .Name = PluginName, .FirstRun = Date.Now.ToString("o")}
            SaveGrants(AutoG)
            Return
        End If

        '构建权限清单文本
        Dim PermList As New Text.StringBuilder
        For Each Perm In ValidPerms
            Dim Info = AllPermissions(Perm)
            PermList.AppendLine("• " & Info.Label & "：" & Info.Description)
        Next

        '先问「全部授予」还是「逐项选择」
        Dim G As New PluginGrants With {.Name = PluginName, .FirstRun = Date.Now.ToString("o")}

        Dim Choice = MyMsgBox(
            "插件「" & PluginName & "」声明需要以下权限：" & vbCrLf & vbCrLf &
            PermList.ToString() & vbCrLf &
            "你可以全部授予，也可以逐项选择。",
            PluginName & " · 权限确认", "全部授予", "逐项选择")

        If Choice = 1 Then
            '全部授予
            For Each Perm In ValidPerms
                G.Granted.Add(Perm)
            Next
            '未声明的权限默认拒绝
            For Each Key In AllPermissions.Keys
                If Not G.Granted.Contains(Key) Then G.Denied.Add(Key)
            Next
            SaveGrants(G)
            Logger.Info($"[权限] {PluginName}：全部授予（{String.Join(", ", G.Granted)}）")
        Else
            '逐项选择：每项弹一次对话框
            For Each Perm In ValidPerms
                Dim Info = AllPermissions(Perm)
                Dim Ok = MyMsgBox(
                    Info.Label & vbCrLf & Info.Description & vbCrLf & vbCrLf &
                    "授予此权限吗？",
                    PluginName & " · " & Info.Label, "授予", "拒绝")
                If Ok = 1 Then
                    G.Granted.Add(Perm)
                Else
                    G.Denied.Add(Perm)
                End If
            Next
            SaveGrants(G)
            Logger.Info($"[权限] {PluginName}：逐项授权完成（已授 {G.Granted.Count}，已拒 {G.Denied.Count}）")
        End If
    End Sub

    ''' <summary>把插件 action 映射到权限类别。</summary>
    Public Shared Function ActionToPermission(Action As String) As String
        Select Case Action
            Case "register_page", "add_button", "modify_ui"
                Return "ui"
            Case "send_command", "console_command"
                Return "console"
            Case "read_config", "write_config"
                Return "config"
            Case "http_request", "download"
                Return "network"
            Case "read_file", "write_file", "delete_file"
                Return "filesystem"
            Case "create_backup", "restore_backup"
                Return "backup"
            Case Else
                Return ""
        End Select
    End Function

End Class
