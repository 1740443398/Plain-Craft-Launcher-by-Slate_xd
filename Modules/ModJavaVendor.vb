Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq

''' <summary>
''' Java 发行版适配：在 Mojang 原版 JDK 之外，接入 Adoptium（Eclipse Temurin）。
''' <para>
''' <b>为什么需要别的发行版</b>：Mojang 只提供它自己认证过的那几个 JDK。
''' 但玩家实际会遇到的情况更多：
''' </para>
''' <list type="bullet">
''' <item>系统里装的是 Temurin / Zulu / Liberica，不是 Mojang 那个</item>
''' <item>Mojang 的 JDK 在某些 CPU（老 AMD、ARM 设备）上跑不起来</item>
''' <item>玩家想用更新的 JDK 获得性能提升</item>
''' </list>
''' <para>
''' <b>为什么优先 Adoptium</b>：它是 Eclipse 基金会维护的 OpenJDK 构建，
''' 有公开 API、有 SHA256、覆盖 Windows/macOS/Linux 与 x64/aarch64，
''' 是目前最主流的免费发行版。
''' </para>
''' </summary>
Public Module ModJavaVendor

    ''' <summary>Adoptium API 根地址。</summary>
    Public Const AdoptiumApi As String = "https://api.adoptium.net/v3"

    ''' <summary>一个可下载的 Java 发行版。</summary>
    Public Class VendorRelease
        Public Property Vendor As String = ""
        Public Property Version As Integer = 0
        Public Property ImageType As String = ""
        Public Property Os As String = ""
        Public Property Arch As String = ""
        Public Property DownloadUrl As String = ""
        Public Property Sha256 As String = ""
        Public Property Size As Long = 0
        Public Property ReleaseName As String = ""

        Public ReadOnly Property DisplayText As String
            Get
                Return $"{Vendor} {Version} ({ImageType})"
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 当前系统对应的 Adoptium OS 标识。
    ''' </summary>
    Public ReadOnly Property CurrentOs As String
        Get
            Select Case Environment.OSVersion.Platform
                Case PlatformID.Win32NT, PlatformID.Win32Windows, PlatformID.Win32S, PlatformID.WinCE
                    Return "windows"
                Case PlatformID.MacOSX
                    Return "mac"
                Case Else
                    Return "linux"
            End Select
        End Get
    End Property

    ''' <summary>当前系统架构标识。</summary>
    Public ReadOnly Property CurrentArch As String
        Get
            '优先用环境变量判断（比 IntPtr.Size 准，能区分 arm64）
            Dim ProcArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")
            If Not String.IsNullOrWhiteSpace(ProcArch) Then
                Select Case ProcArch.ToUpperInvariant()
                    Case "AMD64" : Return "x64"
                    Case "ARM64" : Return "aarch64"
                    Case "X86" : Return "x86"
                End Select
            End If
            '兜底：按指针宽度猜
            Return If(IntPtr.Size = 8, "x64", "x86")
        End Get
    End Property

    ''' <summary>
    ''' 查询某个主版本可用的 Adoptium 发行版。
    ''' </summary>
    ''' <param name="MajorVersion">Java 主版本，如 8 / 17 / 21。</param>
    Public Function FetchReleases(MajorVersion As Integer) As List(Of VendorRelease)
        Dim Out As New List(Of VendorRelease)
        Try
            '只取 JRE（够跑游戏），jdk 体积大一倍且对玩家没意义
            Dim Url = $"{AdoptiumApi}/assets/latest/{MajorVersion}/hotspot" &
                      $"?architecture={CurrentArch}&image_type=jre&os={CurrentOs}&vendor=eclipse"
            Dim Raw = HttpGet(Url)
            If String.IsNullOrWhiteSpace(Raw) Then Return Out
            Dim Root As JToken = JToken.Parse(Raw)
            Dim Arr As JArray = TryCast(Root, JArray)
            If Arr Is Nothing Then Return Out

            For Each Item In Arr
                Try
                    Dim Pkg = Item("binary")?("package")
                    If Pkg Is Nothing Then Continue For
                    Out.Add(New VendorRelease With {
                        .Vendor = "Adoptium Temurin",
                        .Version = MajorVersion,
                        .ImageType = If(Item("binary")?("image_type")?.ToString(), "jre"),
                        .Os = CurrentOs,
                        .Arch = CurrentArch,
                        .DownloadUrl = If(Pkg("link")?.ToString(), ""),
                        .Sha256 = If(Pkg("checksum")?.ToString(), ""),
                        .Size = If(Pkg("size")?.ToObject(Of Long)(), 0L),
                        .ReleaseName = If(Item("release_name")?.ToString(), "")
                    })
                Catch ex2 As Exception
                    Logger.Warn(ex2, "解析 Adoptium 条目失败")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"查询 Adoptium Java {MajorVersion} 失败")
        End Try
        Return Out
    End Function

    ''' <summary>
    ''' 检查当前系统的 Java 是否来自非 Mojang 发行版。
    ''' </summary>
    ''' <param name="JavaPath">java.exe 路径。</param>
    Public Function DetectVendor(JavaPath As String) As String
        Try
            If String.IsNullOrWhiteSpace(JavaPath) Then Return "未知"
            Dim Dir = System.IO.Path.GetDirectoryName(JavaPath)
            If String.IsNullOrWhiteSpace(Dir) Then Return "未知"

            '常见发行版的安装目录特征
            Dim P = Dir.ToLowerInvariant()
            If P.Contains("temurin") Then Return "Eclipse Temurin"
            If P.Contains("adoptium") Then Return "Eclipse Temurin"
            If P.Contains("zulu") Then Return "Azul Zulu"
            If P.Contains("liberica") Then Return "BellSoft Liberica"
            If P.Contains("corretto") Then Return "Amazon Corretto"
            If P.Contains("graalvm") Then Return "GraalVM"
            If P.Contains("microsoft") Then Return "Microsoft OpenJDK"
            If P.Contains("sapmachine") Then Return "SapMachine"
            If P.Contains("oracle") Then Return "Oracle JDK"
            If P.Contains("jdk-") OrElse P.Contains("jre-") Then Return "OpenJDK"

            'Mojang 自带的一般在 runtime 目录
            If P.Contains("\.minecraft\runtime\") Then Return "Mojang 原版"

            Return "未知"
        Catch ex As Exception
            Logger.Warn(ex, "检测 Java 发行版失败")
            Return "未知"
        End Try
    End Function

    Private Function HttpGet(Url As String) As String
        Try
            Net.ServicePointManager.SecurityProtocol = Net.ServicePointManager.SecurityProtocol Or
                                                       Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
            Dim Req As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Req.Method = "GET"
            Req.Timeout = 20000
            Req.ReadWriteTimeout = 20000
            Req.UserAgent = "SLATCL/" & VersionBaseName
            Req.Accept = "application/json"
            Req.AutomaticDecompression = Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                Using Reader As New IO.StreamReader(Resp.GetResponseStream(), Text.Encoding.UTF8)
                    Return Reader.ReadToEnd()
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"请求失败：{Url}")
            Return Nothing
        End Try
    End Function


''' <summary>
''' 下载并安装一个 Adoptium JRE 到指定目录。
''' <para>
''' 解压后返回 java.exe 的完整路径，可直接加入 Java 列表。
''' </para>
''' </summary>
''' <param name="MajorVersion">Java 主版本，如 17。</param>
''' <param name="OnProgress">进度回调，参数为 0~1。</param>
    Public Function DownloadAndInstall(MajorVersion As Integer, Optional OnProgress As Action(Of Double) = Nothing) As String
        Try
            Dim Releases = FetchReleases(MajorVersion)
            If Releases.Count = 0 Then
                Hint($"没有找到适用于当前系统的 Java {MajorVersion}", HintType.Red)
                Return Nothing
            End If

            Dim Rel = Releases(0)
            Logger.Info($"准备安装 {Rel.DisplayText}：{Rel.DownloadUrl}")

            '下载
            Dim Data = HttpGetBytes(Rel.DownloadUrl, OnProgress)
            If Data Is Nothing OrElse Data.Length < 1024 * 1024 Then
                Hint("Java 下载失败", HintType.Red)
                Return Nothing
            End If

            '校验
            If Not String.IsNullOrWhiteSpace(Rel.Sha256) Then
                Try
                    Using Sha = Security.Cryptography.SHA256.Create()
                        Dim Actual = BitConverter.ToString(Sha.ComputeHash(Data)).Replace("-", "").ToLowerInvariant()
                        If Not String.Equals(Actual, Rel.Sha256.Trim(), StringComparison.OrdinalIgnoreCase) Then
                            Hint("Java 下载校验失败，请重试", HintType.Red)
                            Return Nothing
                        End If
                        Logger.Info("Java SHA256 校验通过")
                    End Using
                Catch ex2 As Exception
                    Logger.Warn(ex2, "Java 校验出错，跳过")
                End Try
            End If

            '解压到 PCL\Java\<版本>
            Dim TargetDir = System.IO.Path.Combine(Paths.Base, "PCL", "Java", $"temurin-{MajorVersion}")
            DirectoryUtils.Create(TargetDir)
            Dim TempZip = System.IO.Path.Combine(TargetDir, "download.zip")
            IO.File.WriteAllBytes(TempZip, Data)
            Try
                System.IO.Compression.ZipFile.ExtractToDirectory(TempZip, TargetDir)
            Finally
                Try
                    If FileUtils.Exists(TempZip) Then FileUtils.Delete(TempZip, True)
                Catch
                End Try
            End Try

            '找 java.exe（zip 里通常多一层目录）
            Dim JavaExe = FindJavaExe(TargetDir)
            If String.IsNullOrWhiteSpace(JavaExe) Then
                Hint("解压后没有找到 java.exe", HintType.Red)
                Return Nothing
            End If

            Logger.Info($"Java {MajorVersion} 已安装：{JavaExe}")
            Hint($"Java {MajorVersion} 已安装", HintType.Green)
            Return JavaExe
        Catch ex As Exception
            Logger.Error(ex, $"安装 Java {MajorVersion} 失败")
            Hint($"安装失败：{ex.Message}", HintType.Red)
            Return Nothing
        End Try
    End Function

''' <summary>在目录里递归找 java.exe。</summary>
    Private Function FindJavaExe(Root As String) As String
        Try
            If Not DirectoryUtils.Exists(Root) Then Return Nothing
            '先看常见位置
            Dim Candidates = {
                System.IO.Path.Combine(Root, "bin", "java.exe"),
                System.IO.Path.Combine(Root, "bin", "java")
            }
            For Each C In Candidates
                If FileUtils.Exists(C) Then Return C
            Next
            '再递归找（zip 通常多一层 目录名）
            For Each Sub_ In System.IO.Directory.GetDirectories(Root)
                Dim Found = FindJavaExe(Sub_)
                If Not String.IsNullOrWhiteSpace(Found) Then Return Found
            Next
            Return Nothing
        Catch
            Return Nothing
        End Try
    End Function

    Private Function HttpGetBytes(Url As String, Optional OnProgress As Action(Of Double) = Nothing) As Byte()
        Try
            Net.ServicePointManager.SecurityProtocol = Net.ServicePointManager.SecurityProtocol Or
                                                       Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
            Dim Req As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Req.Method = "GET"
            Req.Timeout = 300000
            Req.ReadWriteTimeout = 300000
            Req.UserAgent = "SLATCL/" & VersionBaseName
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                Dim Total = Resp.ContentLength
                Using Src = Resp.GetResponseStream()
                    Using Ms As New IO.MemoryStream()
                        Dim Buf(81919) As Byte
                        Dim Read As Integer
                        Do
                            Read = Src.Read(Buf, 0, Buf.Length)
                            If Read <= 0 Then Exit Do
                            Ms.Write(Buf, 0, Read)
                            If OnProgress IsNot Nothing AndAlso Total > 0 Then
                                Try
                                    OnProgress(CDbl(Ms.Length) / Total)
                                Catch
                                End Try
                            End If
                        Loop
                        Return Ms.ToArray()
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"下载失败：{Url}")
            Return Nothing
        End Try
    End Function
End Module
