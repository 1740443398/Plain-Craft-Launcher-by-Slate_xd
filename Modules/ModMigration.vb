''' <summary>
''' 版本迁移：服务端升级 MC 版本时，扫描 mods 目录里的 jar，
''' 解析 mod ID，查 Modrinth 看目标版本有没有对应更新，
''' 有就批量下载到新版本的 mods 目录。没对应版本的不动，明确告知。
'''
''' Modrinth API（免费无 key）：
'''   GET /v2/project/{slug}/version?game_versions=["1.21"]&loaders=["fabric"]
'''   返回按时间倒序的版本列表，第一个通常就是最新。
''' </summary>
Imports System.Collections.Generic
Imports System.IO.Compression

Public Module ModMigration

    ''' <summary>一个待迁移的 mod 条目。</summary>
    Public Class MigrationItem
        Public Property FileName As String = ""          '当前文件名
        Public Property ModId As String = ""             'Modrinth slug / mod ID
        Public Property ModName As String = ""           '显示名
        Public Property CurrentVersion As String = ""    '当前版本号
        Public Property TargetVersion As String = ""     '目标 MC 版本上的最新版本号
        Public Property DownloadUrl As String = ""
        Public Property TargetFileName As String = ""
        Public Property Loader As String = ""            'fabric / forge / neoforge
        Public Property CanMigrate As Boolean = False    '目标版本有没有更新
    End Class

    ''' <summary>
    ''' 扫描 mods 目录，解析每个 jar 的 mod 元数据，查 Modrinth 目标版本可用性。
    ''' 返回迁移报告。解析失败或网络失败的条目标记为不可迁移。
    ''' </summary>
    Public Function ScanForMigration(ModsDir As String, TargetMcVersion As String, Loader As String) As List(Of MigrationItem)
        Dim Out As New List(Of MigrationItem)
        If Not IO.Directory.Exists(ModsDir) Then Return Out

        For Each JarFile In IO.Directory.GetFiles(ModsDir, "*.jar")
            Try
                Dim Item = ParseJarMeta(JarFile, Loader)
                If Item Is Nothing Then Continue For

                '查 Modrinth：同 mod 在目标 MC 版本的最新构建
                Dim Query = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(Item.ModId)}/version" &
                    $"?game_versions=%5B%22{Uri.EscapeDataString(TargetMcVersion)}%22%5D" &
                    $"&loaders=%5B%22{Uri.EscapeDataString(Loader)}%22%5D"
                Dim Raw = HttpGetJson(Query)
                If Raw IsNot Nothing Then
                    Dim Arr = Newtonsoft.Json.Linq.JArray.Parse(Raw)
                    If Arr.Count > 0 Then
                        Dim Latest = Arr(0)
                        Item.TargetVersion = Latest("version_number")?.ToString()
                        Item.TargetFileName = ""
                        '取第一个 .jar 文件的下载链接
                        For Each F In Latest("files")
                            If F("filename")?.ToString().EndsWith(".jar") Then
                                Item.TargetFileName = F("filename")?.ToString()
                                Item.DownloadUrl = F("url")?.ToString()
                                Exit For
                            End If
                        Next
                        Item.CanMigrate = Item.DownloadUrl <> ""
                    End If
                End If
                Out.Add(Item)
            Catch ex As Exception
                Logger.Warn(ex, $"解析 mod 文件失败：{JarFile}")
            End Try
        Next
        Return Out
    End Function

    ''' <summary>从 jar 里读 fabric.mod.json 或 META-INF/mods.toml 拿 mod_id。</summary>
    Public Function ParseJarMeta(JarPath As String, Loader As String) As MigrationItem
        Try
            Using Zip As New IO.Compression.ZipArchive(IO.File.OpenRead(JarPath), IO.Compression.ZipArchiveMode.Read)
                Dim ModId = ""
                Dim ModName = ""
                Dim ModVer = ""

                'Fabric: fabric.mod.json
                Dim FEntry = Zip.GetEntry("fabric.mod.json")
                If FEntry IsNot Nothing Then
                    Using R As New IO.StreamReader(FEntry.Open())
                        Dim Json = Newtonsoft.Json.Linq.JObject.Parse(R.ReadToEnd())
                        ModId = Json("id")?.ToString()
                        ModName = Json("name")?.ToString()
                        ModVer = Json("version")?.ToString()
                    End Using
                End If

                'Forge/NeoForge: META-INF/mods.toml 或 META-INF/neoforge.mods.toml
                If ModId = "" Then
                    For Each MetaPath In {"META-INF/mods.toml", "META-INF/neoforge.mods.toml"}
                        Dim TEntry = Zip.GetEntry(MetaPath)
                        If TEntry Is Nothing Then Continue For
                        Using R As New IO.StreamReader(TEntry.Open())
                            Dim Text = R.ReadToEnd()
                            'TOML 里第一个 [[mods]] 段的 modId="..."
                            Dim M = System.Text.RegularExpressions.Regex.Match(Text, "modId\s*=\s*""([^""]+)""")
                            If M.Success Then ModId = M.Groups(1).Value
                            M = System.Text.RegularExpressions.Regex.Match(Text, "displayName\s*=\s*""([^""]+)""")
                            If M.Success Then ModName = M.Groups(1).Value
                            M = System.Text.RegularExpressions.Regex.Match(Text, "version\s*=\s*""([^""]+)""")
                            If M.Success Then ModVer = M.Groups(1).Value
                            Exit For
                        End Using
                    Next
                End If

                If ModId = "" Then Return Nothing

                Return New MigrationItem With {
                    .FileName = IO.Path.GetFileName(JarPath),
                    .ModId = ModId,
                    .ModName = If(ModName, ModId),
                    .CurrentVersion = ModVer,
                    .Loader = Loader}
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"读取 jar 元数据失败：{JarPath}")
            Return Nothing
        End Try
    End Function

    ''' <summary>下载迁移后的 mod jar 到目标目录。</summary>
    Public Function DownloadMigrated(Item As MigrationItem, TargetModsDir As String) As Boolean
        Try
            IO.Directory.CreateDirectory(TargetModsDir)
            If String.IsNullOrWhiteSpace(Item.TargetFileName) Then Return False
            Dim Dest = IO.Path.Combine(TargetModsDir, Item.TargetFileName)
            'CDN 同样只支持 TLS 1.2+
            Try
                Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
            Catch
            End Try
            Dim Req = CType(Net.WebRequest.Create(Item.DownloadUrl), Net.HttpWebRequest)
            Req.Method = "GET"
            Req.Timeout = 600000
            Req.ReadWriteTimeout = 600000
            Req.UserAgent = "SLATCL/0.1"
            Using Resp = Req.GetResponse()
                Using S = Resp.GetResponseStream()
                    Using F As New IO.FileStream(Dest, IO.FileMode.Create)
                        Dim Buf(65535) As Byte
                        While True
                            Dim N = S.Read(Buf, 0, Buf.Length)
                            If N <= 0 Then Exit While
                            F.Write(Buf, 0, N)
                        End While
                    End Using
                End Using
            End Using
            Return IO.File.Exists(Dest) AndAlso New IO.FileInfo(Dest).Length > 1024
        Catch ex As Exception
            Logger.Error(ex, $"下载迁移 mod 失败：{Item.ModId}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 迁移配置文件和资源包（升级 MC 版本时它们基本跨版本兼容）。
    ''' 搬运 config / resourcepacks / kubejs / defaultconfigs 四个目录。
    ''' 资源包的 pack.mcmeta 会自动更新 pack_format 到目标版本。
    ''' 已存在的文件不覆盖（新版 mod 可能生成了新配置项）。
    ''' </summary>
    Public Sub MigrateConfigsAndPacks(OldDir As String, NewDir As String, McVersion As String)
        Try
            Dim MigrateDirs = {"config", "resourcepacks", "kubejs", "defaultconfigs"}
            For Each Sub2 In MigrateDirs
                Dim Src = IO.Path.Combine(OldDir, Sub2)
                If Not IO.Directory.Exists(Src) Then Continue For
                Dim Dst = IO.Path.Combine(NewDir, Sub2)
                IO.Directory.CreateDirectory(Dst)
                Dim Copied = 0
                CopyDirectoryContents(Src, Dst, Copied)
                If Copied > 0 Then Logger.Info($"迁移 {Sub2}: {Copied} 个文件")
            Next

            '资源包：更新 pack.mcmeta 的 pack_format（客户端据此判断兼容性）
            Dim Pf = TargetPackFormat(McVersion)
            UpdatePackFormats(IO.Path.Combine(NewDir, "resourcepacks"), Pf)
        Catch ex As Exception
            Logger.Error(ex, "迁移配置文件和资源包失败")
        End Try
    End Sub

    ''' <summary>
    ''' 获取可选的正式版 MC 版本号列表（新→旧）。
    ''' 只请求 version_manifest_v2.json 一次，不逐个拉版本详情——
    ''' 迁移只需要版本字符串，不需要下载地址。
    ''' </summary>
    Public Function FetchTargetVersions(Optional IncludeSnapshot As Boolean = False, Optional Limit As Integer = 60) As List(Of String)
        Dim Out As New List(Of String)
        Try
            Dim Manifest = HttpGetJson("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json")
            If Manifest Is Nothing Then Return Out
            Dim Root = Newtonsoft.Json.Linq.JObject.Parse(Manifest)
            Dim All = Root("versions")
            If All Is Nothing Then Return Out
            For Each Item In All
                Dim T = Item("type")?.ToString()
                If T <> "release" AndAlso Not (IncludeSnapshot AndAlso T = "snapshot") Then Continue For
                Dim Id = Item("id")?.ToString()
                If String.IsNullOrWhiteSpace(Id) Then Continue For
                Out.Add(Id)
                If Out.Count >= Limit Then Exit For
            Next
        Catch ex As Exception
            Logger.Warn(ex, "获取 MC 版本列表失败")
        End Try
        Return Out
    End Function

    ''' <summary>
    ''' 从版本列表里推断 loader 标识（Modrinth 用的名字）。
    ''' 判断不出来时返回空串，调用方应让用户手动选。
    ''' </summary>
    Public Function GuessLoader(Instance As McInstance) As String
        Try
            If Instance Is Nothing OrElse Instance.Version Is Nothing Then Return ""
            If Instance.Version.HasFabric Then Return "fabric"
            If Instance.Version.HasNeoForge Then Return "neoforge"
            If Instance.Version.HasForge Then Return "forge"
        Catch ex As Exception
            Logger.Warn(ex, "推断 loader 失败")
        End Try
        Return ""
    End Function

    ''' <summary>递归复制目录内容。已存在的文件不覆盖。</summary>
    Private Sub CopyDirectoryContents(Src As String, Dst As String, ByRef Copied As Integer)
        Try
            IO.Directory.CreateDirectory(Dst)
            For Each F In IO.Directory.GetFiles(Src)
                Dim Target = IO.Path.Combine(Dst, IO.Path.GetFileName(F))
                If Not IO.File.Exists(Target) Then
                    IO.File.Copy(F, Target)
                    Copied += 1
                End If
            Next
            For Each D In IO.Directory.GetDirectories(Src)
                CopyDirectoryContents(D, IO.Path.Combine(Dst, IO.Path.GetFileName(D)), Copied)
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"复制目录失败：{Src} -> {Dst}")
        End Try
    End Sub

    ''' <summary>更新 resourcepacks 下所有 pack.mcmeta 的 pack_format 字段。</summary>
    Private Sub UpdatePackFormats(PacksDir As String, NewFormat As Integer)
        Try
            If Not IO.Directory.Exists(PacksDir) Then Return
            For Each Mcmeta In IO.Directory.GetFiles(PacksDir, "pack.mcmeta", IO.SearchOption.AllDirectories)
                Try
                    Dim Text = IO.File.ReadAllText(Mcmeta)
                    Dim Pat = Chr(34) & "pack_format" & Chr(34) & "\s*:\s*\d+"
                    Dim Repl = Chr(34) & "pack_format" & Chr(34) & ": " & NewFormat.ToString()
                    Dim Updated = System.Text.RegularExpressions.Regex.Replace(Text, Pat, Repl)
                    If Updated <> Text Then
                        IO.File.WriteAllText(Mcmeta, Updated)
                        Logger.Info($"已更新 pack_format: {Mcmeta}")
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, $"更新 pack_format 失败：{Mcmeta}")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, "批量更新 pack_format 失败")
        End Try
    End Sub

    ''' <summary>目标 MC 版本对应的 pack_format。升级版本时资源包用这个数。</summary>
    ''' <summary>
    ''' 目标版本对应的资源包 pack_format。
    ''' 映射依据 Mojang 官方变更表（资源包，非数据包），必须精确到 patch 号，
    ''' 因为同一个 1.20.x 内 1.20.1 / 1.20.2 / 1.20.5 的 pack_format 各不相同。
    ''' </summary>
    Public Function TargetPackFormat(McVersion As String) As Integer
        If String.IsNullOrWhiteSpace(McVersion) Then Return 15
        Dim M = System.Text.RegularExpressions.Regex.Match(McVersion, "^(\d+)\.(\d+)(?:\.(\d+))?")
        If Not M.Success Then Return 15

        Dim Maj = CInt(M.Groups(1).Value)
        Dim Min = CInt(M.Groups(2).Value)
        Dim Pat = If(M.Groups(3).Success, CInt(M.Groups(3).Value), 0)

        '版本键 = 主*10000 + 次*100 + patch，便于按数值区间比较
        Dim K = Maj * 10000 + Min * 100 + Pat

        Select Case True
            Case K >= 12105 : Return 55   '1.21.5+
            Case K >= 12104 : Return 46   '1.21.4
            Case K >= 12102 : Return 42   '1.21.2 - 1.21.3
            Case K >= 12100 : Return 34   '1.21 - 1.21.1
            Case K >= 12005 : Return 32   '1.20.5 - 1.20.6
            Case K >= 12003 : Return 22   '1.20.3 - 1.20.4
            Case K >= 12002 : Return 18   '1.20.2
            Case K >= 12000 : Return 15   '1.20 - 1.20.1
            Case K >= 11904 : Return 15   '1.19.4
            Case K >= 11903 : Return 13   '1.19.3
            Case K >= 11900 : Return 12   '1.19 - 1.19.2
            Case K >= 11802 : Return 9    '1.18.2+
            Case K >= 11800 : Return 8    '1.18 - 1.18.1
            Case K >= 11700 : Return 7    '1.17.x
            Case Else : Return 6          '1.16.x 及更早
        End Select
    End Function

    ''' <summary>把旧版本 mods 目录里的对应 jar 删掉（迁移完成后清理旧版本）。</summary>
    Public Sub RemoveOldMod(ModsDir As String, FileName As String)
        Try
            Dim P = IO.Path.Combine(ModsDir, FileName)
            If IO.File.Exists(P) Then IO.File.Delete(P)
        Catch ex As Exception
            Logger.Warn(ex, $"删除旧 mod 失败：{FileName}")
        End Try
    End Sub

    Function HttpGetJson(Url As String) As String
        Try
            'Modrinth 与 Mojang 都已停用 TLS 1.0/1.1，而 .NET Framework 4.8 默认只启 Ssl3+Tls，
            '不显式打开 TLS 1.2 会直接抛「基础连接已经关闭」。
            Try
                Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
            Catch
                '老系统可能没有 Tls12 枚举值，忽略即可
            End Try
            Dim Req = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Req.Timeout = 15000
            Req.ReadWriteTimeout = 15000
            Req.UserAgent = "SLATCL/0.1"
            Using Resp = Req.GetResponse()
                Using R As New IO.StreamReader(Resp.GetResponseStream(), System.Text.Encoding.UTF8)
                    Return R.ReadToEnd()
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"请求失败：{Url}")
            Return Nothing
        End Try
    End Function
End Module
