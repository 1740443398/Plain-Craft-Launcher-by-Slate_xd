Imports System.Collections.Generic

''' <summary>
''' 插件目录保护。
'''
''' 要防的三件事：
'''   1. 别的插件偷偷改某个插件的 plugin.json，把自己的权限声明改大；
'''   2. 别的插件改 Plugins\.permissions\*.json，给自己授权；
'''   3. 未授权的插件被"放行"运行。
'''
''' 三层防线：
'''   1. 完整性清单（.integrity.json）：记录每个插件 plugin.json + entry jar 的 SHA256。
'''      加载前比对，变了就标红并强制用户重新授权。
'''   2. 权限存储自保护：.permissions 目录被标记为受保护，
'''      任何插件对这些文件的写操作在运行时被硬拦截（不看授权，一律拒绝）。
'''   3. 路径沙箱：插件的 filesystem 操作只能落在自己的目录内；
'''      指向 Plugins\ 下其它位置的路径一律拒绝。
''' </summary>
Public Class PluginProtection

    ''' <summary>受保护子目录（插件永远不能写）。</summary>
    Public Shared ReadOnly ProtectedNames As String() = {".permissions", ".integrity.json", ".lock"}

    ''' <summary>完整性清单路径。</summary>
    Public Shared ReadOnly Property ManifestPath As String
        Get
            Return IO.Path.Combine(ModPluginHost.PluginDir, ".integrity.json")
        End Get
    End Property

    ''' <summary>某个插件是否被判定为「被篡改」。</summary>
    Public Class IntegrityResult
        Public Property PluginName As String = ""
        Public Property Ok As Boolean = True
        Public Property Reason As String = ""
        Public Property ChangedFiles As New List(Of String)
    End Class

    ''' <summary>算一个文件的 SHA256（小写十六进制）。</summary>
    Public Shared Function FileSha256(Path As String) As String
        Try
            Using Sha As Security.Cryptography.SHA256 = Security.Cryptography.SHA256.Create()
                Using Fs = IO.File.OpenRead(Path)
                    Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "").ToLowerInvariant()
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"计算哈希失败：{Path}")
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 为单个插件生成指纹（plugin.json + entry jar）。
    ''' </summary>
    Public Shared Function Fingerprint(P As SlatPlugin) As String
        Try
            Dim MetaPath = IO.Path.Combine(P.Dir, "plugin.json")
            Dim EntryPath = IO.Path.Combine(P.Dir, P.Entry)
            Dim MetaHash = If(IO.File.Exists(MetaPath), FileSha256(MetaPath), "")
            Dim EntryHash = If(IO.File.Exists(EntryPath), FileSha256(EntryPath), "")
            Return $"{MetaHash}:{EntryHash}"
        Catch ex As Exception
            Logger.Warn(ex, $"生成插件指纹失败：{P.Name}")
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 校验所有插件的完整性。返回被篡改的插件列表（空 = 全部正常）。
    ''' </summary>
    Public Shared Function VerifyAll() As List(Of IntegrityResult)
        Dim Bad As New List(Of IntegrityResult)
        Try
            Dim Manifest As Newtonsoft.Json.Linq.JObject = Nothing
            If IO.File.Exists(ManifestPath) Then
                Try
                    Manifest = Newtonsoft.Json.Linq.JObject.Parse(IO.File.ReadAllText(ManifestPath))
                Catch ex As Exception
                    Logger.Warn(ex, "完整性清单损坏，将重建")
                End Try
            End If

            For Each P In ModPluginHost.Plugins
                Dim R As New IntegrityResult With {.PluginName = P.Name, .Ok = True}
                Dim Current = Fingerprint(P)
                If Manifest IsNot Nothing AndAlso Manifest(P.Name) IsNot Nothing Then
                    Dim Recorded = Manifest(P.Name)("fingerprint")?.ToString()
                    If Not String.IsNullOrWhiteSpace(Recorded) AndAlso Recorded <> Current Then
                        R.Ok = False
                        R.Reason = "plugin.json 或 jar 已被修改（指纹不匹配）"
                        R.ChangedFiles.Add("plugin.json / " & P.Entry)
                        Logger.Warn($"[保护] 插件 {P.Name} 指纹不匹配：{Recorded} → {Current}")
                    End If
                End If
                If Not R.Ok Then Bad.Add(R)
            Next
        Catch ex As Exception
            Logger.Error(ex, "校验插件完整性失败")
        End Try
        Return Bad
    End Function

    ''' <summary>
    ''' 把当前所有插件的指纹写入清单（安装/更新后调用）。
    ''' </summary>
    Public Shared Sub SaveManifest()
        Try
            Dim J As New Newtonsoft.Json.Linq.JObject
            For Each P In ModPluginHost.Plugins
                J(P.Name) = New Newtonsoft.Json.Linq.JObject From {
                    {"fingerprint", Fingerprint(P)},
                    {"version", P.Version},
                    {"recorded_at", Date.Now.ToString("o")}}
            Next
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(ManifestPath))
            IO.File.WriteAllText(ManifestPath, J.ToString(Newtonsoft.Json.Formatting.Indented))
            Logger.Info($"[保护] 完整性清单已更新：{ModPluginHost.Plugins.Count} 个插件")
        Catch ex As Exception
            Logger.Error(ex, "写入完整性清单失败")
        End Try
    End Sub

    ''' <summary>
    ''' 判断一个路径是否落在「受保护区域」——是则任何插件都不能写。
    ''' 受保护区域 = Plugins\.permissions\*、Plugins\.integrity.json、
    '''             以及「不是自己目录」的其它插件目录。
    ''' </summary>
    Public Shared Function IsProtectedPath(PluginName As String, TargetPath As String) As Boolean
        Try
            If String.IsNullOrWhiteSpace(TargetPath) Then Return True
            Dim Full = IO.Path.GetFullPath(TargetPath)
            Dim Root = IO.Path.GetFullPath(ModPluginHost.PluginDir).TrimEnd(IO.Path.DirectorySeparatorChar) & IO.Path.DirectorySeparatorChar

            '不在 Plugins 下：不受本保护约束（由 filesystem 权限管）
            If Not Full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then Return False

            Dim Rel = Full.Substring(Root.Length)
            Dim FirstSeg = Rel.Split(IO.Path.DirectorySeparatorChar)(0)

            '① 权限存储与清单：一律保护
            If ProtectedNames.Contains(FirstSeg, StringComparer.OrdinalIgnoreCase) Then
                Logger.Warn($"[保护] 插件 {PluginName} 试图访问受保护路径：{Full}")
                Return True
            End If

            '② 别的插件的目录：保护
            If Not String.Equals(FirstSeg, PluginName, StringComparison.OrdinalIgnoreCase) Then
                Logger.Warn($"[保护] 插件 {PluginName} 试图访问其它插件目录：{Full}")
                Return True
            End If

            '③ 自己目录内：允许
            Return False
        Catch ex As Exception
            Logger.Warn(ex, "路径保护检查失败，按保护处理")
            Return True
        End Try
    End Function

    ''' <summary>
    ''' 文件系统加固：把 .permissions 目录和清单设为只读 + 隐藏，
    ''' 降低被随手改坏的概率（不是安全边界，安全边界是上面的运行时拦截）。
    ''' </summary>
    Public Shared Sub HardenStorage()
        Try
            Dim PermDir = IO.Path.Combine(ModPluginHost.PluginDir, ".permissions")
            IO.Directory.CreateDirectory(PermDir)
            IO.File.SetAttributes(PermDir, IO.FileAttributes.Directory Or IO.FileAttributes.Hidden)
            For Each F In IO.Directory.GetFiles(PermDir)
                IO.File.SetAttributes(F, IO.FileAttributes.ReadOnly Or IO.FileAttributes.Hidden)
            Next
            If IO.File.Exists(ManifestPath) Then
                IO.File.SetAttributes(ManifestPath, IO.FileAttributes.ReadOnly Or IO.FileAttributes.Hidden)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "加固权限存储失败（不影响功能）")
        End Try
    End Sub

    ''' <summary>
    ''' 写权限文件前先解除只读，写完再加回去。
    ''' </summary>
    Public Shared Sub WithWritable(Path As String, Action As Action)
        Dim WasReadOnly = False
        Try
            If IO.File.Exists(Path) Then
                WasReadOnly = (IO.File.GetAttributes(Path) And IO.FileAttributes.ReadOnly) <> 0
                If WasReadOnly Then
                    IO.File.SetAttributes(Path, IO.File.GetAttributes(Path) And Not IO.FileAttributes.ReadOnly)
                End If
            End If
            Action()
        Finally
            If WasReadOnly AndAlso IO.File.Exists(Path) Then
                Try
                    IO.File.SetAttributes(Path, IO.File.GetAttributes(Path) Or IO.FileAttributes.ReadOnly)
                Catch
                End Try
            End If
        End Try
    End Sub

End Class
