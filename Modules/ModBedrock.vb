Imports System
Imports System.IO
Imports System.Collections
Imports System.Collections.Generic
Imports System.Runtime.InteropServices

''' <summary>
''' 基岩版（Bedrock）检测与启动。
''' <para>
''' <b>与 Java 版的根本区别</b>：
''' Java 版是「一个 jar + 一份 json」，可以自由指定路径、传参、装 mod。
''' 基岩版是 UWP / MSIX 应用，由系统安装管理，启动只能走系统提供的两条路：
''' 协议唤起（minecraft://）或 UWP 激活（IApplicationActivationManager）。
''' </para>
''' <para>
''' <b>本模块不下载、不破解、不绕过授权</b>：
''' 基岩版的获取需要 Microsoft Store 授权，鉴权全部由 Microsoft Store
''' 与 Xbox Live 负责，启动器只负责「把已安装的实例拉起来」。
''' </para>
''' <para>
''' <b>运行时依赖</b>（不需要 NuGet，也不需要引用 Windows.winmd）：
''' WinRT 的 PackageManager 通过 <c>Type.GetType</c> 的 ContentType=WindowsRuntime
''' 投影语法在运行时解析，COM 接口用 <c>ComImport</c> 声明。
''' 这样做的原因：VB.NET 直接引用 Windows.winmd 会引入 netstandard 门面依赖，
''' 容易和项目现有的 .NET Framework 4.8 引用打架；而晚期绑定在
''' .NET Framework 4.8 + Windows 10/11 上实测可用，且不增加任何编译期依赖。
''' </para>
''' <para>
''' <b>所有对外函数都保证「异常不外泄」</b>：任何 WinRT / COM / 注册表失败
''' 都会被捕获并转换成明确的返回值，不会让调用方崩溃。
''' </para>
''' </summary>
Public Module ModBedrock

#Region "公开类型"

    ''' <summary>一个已安装的基岩版 UWP 包。</summary>
    Public Class BedrockPackage
        ''' <summary>包名，例如 Microsoft.MinecraftUWP。</summary>
        Public Property Name As String = ""
        ''' <summary>版本号，例如 1.20.81.1。</summary>
        Public Property Version As String = ""
        ''' <summary>完整包名，包含版本与架构，例如 Microsoft.MinecraftUWP_1.20.81.1_x64__8wekyb3d8bbwe。</summary>
        Public Property PackageFullName As String = ""
        ''' <summary>包家族名，例如 Microsoft.MinecraftUWP_8wekyb3d8bbwe。UWP 激活需要它。</summary>
        Public Property PackageFamilyName As String = ""
        ''' <summary>安装目录。</summary>
        Public Property InstalledLocation As String = ""
        ''' <summary>是否以开发模式（侧载的未签名/自签名包）安装。</summary>
        Public Property IsDevelopmentMode As Boolean = False
        ''' <summary>是否为预览版（Microsoft.MinecraftWindowsBeta）。</summary>
        Public Property IsPreview As Boolean = False
        ''' <summary>包签名类型，来自 WinRT PackageSignatureKind。</summary>
        Public Property SignatureKind As String = ""

        ''' <summary>UWP 应用用户模型 ID（AUMID），固定入口为 !App。</summary>
        Public ReadOnly Property AppUserModelId As String
            Get
                If String.IsNullOrWhiteSpace(PackageFamilyName) Then Return ""
                Return PackageFamilyName & "!App"
            End Get
        End Property

        ''' <summary>给界面显示用的一行文本。</summary>
        Public ReadOnly Property DisplayText As String
            Get
                Dim Tag As String = If(IsPreview, " (Preview)", "")
                Dim NameText As String = If(Name, "").Trim()
                Dim VerText As String = If(Version, "").Trim()
                If NameText.Length = 0 Then NameText = "Minecraft Bedrock"
                If VerText.Length = 0 Then Return NameText & Tag
                Return $"{NameText} {VerText}{Tag}"
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return DisplayText
        End Function
    End Class

    ''' <summary>侧载 / 开发者模式状态。</summary>
    Public Enum BedrockSideloadStatus
        ''' <summary>读取失败，无法判断。</summary>
        Unknown = 0
        ''' <summary>开发者模式与侧载都未开启。</summary>
        None = 1
        ''' <summary>仅开启开发者模式。</summary>
        DeveloperMode = 2
        ''' <summary>仅允许侧载受信任应用。</summary>
        SideloadEnabled = 3
        ''' <summary>开发者模式与侧载都已开启。</summary>
        DeveloperModeAndSideload = 4
    End Enum

    ''' <summary>侧载状态的详细信息，含注册表原始值。</summary>
    Public Class BedrockSideloadInfo
        Public Property Status As BedrockSideloadStatus = BedrockSideloadStatus.Unknown
        Public Property DeveloperMode As Boolean = False
        Public Property SideloadEnabled As Boolean = False
        Public Property RawDeveloperMode As String = ""
        Public Property RawSideload As String = ""
        ''' <summary>注册表键是否读取成功。</summary>
        Public Property KeyReadable As Boolean = False

        Public ReadOnly Property Text As String
            Get
                Select Case Status
                    Case BedrockSideloadStatus.None
                        Return "开发者模式与侧载均未开启"
                    Case BedrockSideloadStatus.DeveloperMode
                        Return "已开启开发者模式"
                    Case BedrockSideloadStatus.SideloadEnabled
                        Return "已允许侧载受信任应用"
                    Case BedrockSideloadStatus.DeveloperModeAndSideload
                        Return "开发者模式与侧载均已开启"
                    Case Else
                        Return "无法判断侧载状态"
                End Select
            End Get
        End Property
    End Class

    ''' <summary>一次启动尝试的结果。</summary>
    Public Class LaunchResult
        Public Property Success As Boolean = False
        ''' <summary>实际走通的唤起方式：activation / protocol / appsFolder。</summary>
        Public Property Mode As String = ""
        Public Property Message As String = ""
        ''' <summary>激活成功时的目标进程 ID。</summary>
        Public Property ProcessId As UInteger = 0
        ''' <summary>COM 返回的 HRESULT；非 COM 路径时为 0。</summary>
        Public Property ErrorCode As Integer = 0

        Public Overrides Function ToString() As String
            If Success Then Return $"OK[{Mode}] {Message}"
            Return $"FAIL[{Mode}] {Message}"
        End Function
    End Class

#End Region

#Region "常量"

    ''' <summary>正式版包家族名。</summary>
    Public Const PackageRetail As String = "Microsoft.MinecraftUWP_8wekyb3d8bbwe"
    ''' <summary>预览版包家族名。</summary>
    Public Const PackagePreview As String = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe"
    ''' <summary>基岩版协议。</summary>
    Public Const ProtocolUri As String = "minecraft://"
    ''' <summary>协议名，不含冒号。</summary>
    Public Const ProtocolScheme As String = "minecraft"
    ''' <summary>UWP 应用入口 ID，Microsoft 基岩版固定为 App。</summary>
    Public Const AppEntryId As String = "App"

    ''' <summary>协议注册的注册表路径（相对 HKEY_CLASSES_ROOT）。</summary>
    Private Const ProtocolCommandPath As String = "minecraft\shell\open\command"
    ''' <summary>侧载开关的注册表路径（相对 HKEY_LOCAL_MACHINE）。</summary>
    Private Const AppModelUnlockPath As String = "SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
    Private Const ValueDeveloperMode As String = "AllowDevelopmentWithoutDevLicense"
    Private Const ValueAllowAllTrustedApps As String = "AllowAllTrustedApps"

    ''' <summary>WinRT PackageManager 的投影类型名。三段式才能让 .NET 解析到 WinRT 类型。</summary>
    Private Const PackageManagerTypeName As String =
        "Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType=WindowsRuntime"

#End Region

#Region "COM 互操作"

    ''' <summary>
    ''' UWP 应用激活接口。
    ''' <para>
    ''' <b>为什么用 PreserveSig</b>：默认的 COM 互操作会把失败 HRESULT 转成异常，
    ''' 丢掉具体错误码；保留原始签名后可以拿到 0x80073CF1（找不到包）这类
    ''' 精确错误并翻译成人话。
    ''' </para>
    ''' <para>
    ''' <b>已知陷阱</b>：<c>appUserModelId</c> 传 Nothing 会让 native 侧
    ''' 直接访问冲突（0xC0000005）导致整个进程静默退出，且无法 Catch。
    ''' 所以调用前必须在托管侧先校验，见 <see cref="LaunchViaActivationDetailed"/>。
    ''' </para>
    ''' </summary>
    <ComImport(),
     Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
    Friend Interface IApplicationActivationManager
        <PreserveSig()>
        Function ActivateApplication(appUserModelId As String,
                                     arguments As String,
                                     options As Integer,
                                     ByRef processId As UInteger) As Integer
    End Interface

    ''' <summary>IApplicationActivationManager 的 CoClass。</summary>
    <ComImport(),
     Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")>
    Friend Class ApplicationActivationManager
    End Class

#End Region

#Region "探测：协议注册"

    ''' <summary>
    ''' 判断 minecraft:// 协议是否已注册。
    ''' <para>
    ''' 检查 <c>HKEY_CLASSES_ROOT\minecraft\shell\open\command</c> 是否存在
    ''' 且默认值非空。注意：<b>UWP 应用的协议处理程序声明在 AppxManifest.xml 里，
    ''' 不一定写进注册表</b>；因此本函数为 False 并不绝对代表协议不可用，
    ''' 完整判断请用 <see cref="IsProtocolAvailable"/>。
    ''' </para>
    ''' </summary>
    Public Function IsProtocolRegistered() As Boolean
        Return Not String.IsNullOrWhiteSpace(GetProtocolCommand())
    End Function

    ''' <summary>
    ''' 取出 minecraft:// 协议关联的命令行。未注册时返回空字符串。
    ''' </summary>
    Public Function GetProtocolCommand() As String
        Try
            Using Key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ProtocolCommandPath)
                If Key Is Nothing Then Return ""
                Dim Cmd As Object = Key.GetValue("")
                If Cmd Is Nothing Then Return ""
                Return Cmd.ToString().Trim()
            End Using
        Catch ex As Exception
            LogWarn(ex, "读取 minecraft:// 协议注册表失败")
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 综合判断 minecraft:// 协议当前是否真的可用。
    ''' <para>
    ''' 优先看注册表；注册表没有时再看已安装包的 AppxManifest.xml
    ''' 是否声明了该协议（UWP 协议走清单注册，不是注册表）。
    ''' </para>
    ''' </summary>
    Public Function IsProtocolAvailable() As Boolean
        Try
            If IsProtocolRegistered() Then Return True
            For Each Pkg In GetInstalledPackages()
                If HasAppxProtocolHandler(Pkg) Then Return True
            Next
            Return False
        Catch ex As Exception
            LogWarn(ex, "判断 minecraft:// 协议可用性失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 检查某个已安装包的 AppxManifest.xml 里是否声明了 minecraft 协议。
    ''' <para>
    ''' 这里是文本检索而不是 XML 解析：清单由系统签名保护，结构固定，
    ''' 只做一次大小写不敏感的子串匹配就足够，且不会因命名空间差异失败。
    ''' </para>
    ''' </summary>
    Public Function HasAppxProtocolHandler(Pkg As BedrockPackage) As Boolean
        Try
            If Pkg Is Nothing Then Return False
            If String.IsNullOrWhiteSpace(Pkg.InstalledLocation) Then Return False
            Dim ManifestPath = Path.Combine(Pkg.InstalledLocation, "AppxManifest.xml")
            If Not File.Exists(ManifestPath) Then Return False
            Dim Text As String = File.ReadAllText(ManifestPath)
            Dim A As String = "Protocol Name=""" & ProtocolScheme & """"
            Dim B As String = "Protocol Name='" & ProtocolScheme & "'"
            If Text.IndexOf(A, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
            If Text.IndexOf(B, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
            Return False
        Catch ex As Exception
            LogWarn(ex, "读取 AppxManifest.xml 失败")
            Return False
        End Try
    End Function

#End Region

#Region "探测：包枚举（WinRT PackageManager）"

    ''' <summary>
    ''' 创建 WinRT 的 <c>Windows.Management.Deployment.PackageManager</c>。
    ''' 系统不支持 winmd 投影时返回 Nothing，不抛异常。
    ''' </summary>
    Private Function CreatePackageManager() As Object
        Try
            Dim T As Type = Type.GetType(PackageManagerTypeName)
            If T Is Nothing Then
                LogInfo("当前系统无法解析 WinRT PackageManager 类型，跳过原生枚举")
                Return Nothing
            End If
            Return Activator.CreateInstance(T)
        Catch ex As Exception
            LogWarn(ex, "创建 WinRT PackageManager 失败")
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 枚举本机已安装的基岩版（正式版 + 预览版）。
    ''' <para>
    ''' 走原生 WinRT <c>PackageManager.FindPackagesForUser</c>，不再启动 PowerShell 子进程。
    ''' 任何一步失败都只记录日志并继续，<b>没有任何包时返回空列表</b>。
    ''' </para>
    ''' </summary>
    Public Function GetInstalledPackages() As List(Of BedrockPackage)
        Dim Result As New List(Of BedrockPackage)
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Dim Pm As Object = CreatePackageManager()
        If Pm Is Nothing Then Return Result

        Dim Families As String() = {PackageRetail, PackagePreview}
        For Each FamilyName As String In Families
            Try
                '两参数重载的第二个参数是「包家族名」，不是包名
                Dim Raw As Object = Pm.FindPackagesForUser("", FamilyName)
                If Raw Is Nothing Then Continue For

                For Each P In DirectCast(Raw, IEnumerable)
                    Try
                        Dim Pkg = ReadPackage(P, FamilyName)
                        If Pkg Is Nothing Then Continue For
                        If String.IsNullOrWhiteSpace(Pkg.PackageFullName) Then Continue For
                        If Seen.Add(Pkg.PackageFullName) Then Result.Add(Pkg)
                    Catch ex As Exception
                        LogWarn(ex, "读取单个基岩版包信息失败")
                    End Try
                Next
            Catch ex As Exception
                LogWarn(ex, $"枚举包家族失败：{FamilyName}")
            End Try
        Next

        Try
            Result.Sort(New BedrockPackageComparer())
        Catch ex As Exception
            LogWarn(ex, "基岩版包列表排序失败")
        End Try

        If Result.Count > 0 Then LogInfo($"已发现 {Result.Count} 个基岩版安装：{String.Join(", ", Result.ConvertAll(Function(P) P.DisplayText))}")
        Return Result
    End Function

    ''' <summary>
    ''' 兼容旧调用点的别名，等价于 <see cref="GetInstalledPackages"/>。
    ''' </summary>
    Public Function ScanInstalled() As List(Of BedrockPackage)
        Return GetInstalledPackages()
    End Function

    ''' <summary>
    ''' 把一个 WinRT Package 对象读成本模块的 <see cref="BedrockPackage"/>。
    ''' 每个属性单独捕获异常：某些包（尤其系统包）的 InstalledLocation 会拒绝访问。
    ''' </summary>
    Private Function ReadPackage(P As Object, FamilyName As String) As BedrockPackage
        If P Is Nothing Then Return Nothing

        Dim Pkg As New BedrockPackage()
        Pkg.IsPreview = String.Equals(FamilyName, PackagePreview, StringComparison.OrdinalIgnoreCase)

        'WinRT 的属性名是 Id.FamilyName；PowerShell 的 Get-AppxPackage 才叫 PackageFamilyName
        Try : Pkg.Name = SafeText(P.Id.Name) : Catch : End Try
        Try : Pkg.PackageFullName = SafeText(P.Id.FullName) : Catch : End Try
        Try : Pkg.PackageFamilyName = SafeText(P.Id.FamilyName) : Catch : End Try
        Try : Pkg.Version = ReadVersion(P) : Catch : End Try
        Try : Pkg.IsDevelopmentMode = CBool(P.IsDevelopmentMode) : Catch : End Try
        Try : Pkg.SignatureKind = SafeText(P.SignatureKind) : Catch : End Try
        Try
            Dim Loc = P.InstalledLocation
            If Loc IsNot Nothing Then Pkg.InstalledLocation = SafeText(Loc.Path)
        Catch
            '系统包或权限不足时读不到安装目录，保留空字符串
        End Try

        '包名兜底：某些环境下 Id.Name 可能为空
        If String.IsNullOrWhiteSpace(Pkg.Name) Then
            Pkg.Name = If(Pkg.IsPreview, "Minecraft Windows Beta", "Minecraft UWP")
        End If
        Return Pkg
    End Function

    ''' <summary>读取 WinRT PackageVersion 结构体为字符串。</summary>
    Private Function ReadVersion(P As Object) As String
        Try
            Dim V As Object = P.Id.Version
            If V Is Nothing Then Return ""
            Return $"{V.Major}.{V.Minor}.{V.Build}.{V.Revision}"
        Catch ex As Exception
            LogWarn(ex, "读取基岩版版本号失败")
            Return ""
        End Try
    End Function

    ''' <summary>把任意晚期绑定值安全转成字符串。</summary>
    Private Function SafeText(Value As Object) As String
        If Value Is Nothing Then Return ""
        Try
            Return Value.ToString()
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>按版本号从高到低排序，版本号无法解析时退化为字符串比较。</summary>
    Private Class BedrockPackageComparer
        Implements IComparer(Of BedrockPackage)

        Public Function Compare(X As BedrockPackage, Y As BedrockPackage) As Integer Implements IComparer(Of BedrockPackage).Compare
            If X Is Nothing AndAlso Y Is Nothing Then Return 0
            If X Is Nothing Then Return 1
            If Y Is Nothing Then Return -1

            Dim Vx As Version = Nothing
            Dim Vy As Version = Nothing
            Dim OkX As Boolean = Version.TryParse(If(X.Version, ""), Vx)
            Dim OkY As Boolean = Version.TryParse(If(Y.Version, ""), Vy)
            If OkX AndAlso OkY Then Return Vy.CompareTo(Vx)
            Return String.Compare(If(Y.Version, ""), If(X.Version, ""), StringComparison.Ordinal)
        End Function
    End Class

#End Region

#Region "探测：侧载 / 开发者模式"

    ''' <summary>读取侧载与开发者模式的详细状态（含注册表原始值）。</summary>
    Public Function GetSideloadInfo() As BedrockSideloadInfo
        Dim Info As New BedrockSideloadInfo()
        Try
            Dim DevRaw As Object = Nothing
            Dim SideRaw As Object = Nothing

            Using Key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(AppModelUnlockPath)
                If Key IsNot Nothing Then
                    Info.KeyReadable = True
                    DevRaw = Key.GetValue(ValueDeveloperMode)
                    SideRaw = Key.GetValue(ValueAllowAllTrustedApps)
                End If
            End Using

            Info.RawDeveloperMode = RawToText(DevRaw)
            Info.RawSideload = RawToText(SideRaw)
            Info.DeveloperMode = RawIsEnabled(DevRaw)
            Info.SideloadEnabled = RawIsEnabled(SideRaw)

            If Info.DeveloperMode AndAlso Info.SideloadEnabled Then
                Info.Status = BedrockSideloadStatus.DeveloperModeAndSideload
            ElseIf Info.DeveloperMode Then
                Info.Status = BedrockSideloadStatus.DeveloperMode
            ElseIf Info.SideloadEnabled Then
                Info.Status = BedrockSideloadStatus.SideloadEnabled
            Else
                Info.Status = BedrockSideloadStatus.None
            End If
            Return Info
        Catch ex As Exception
            LogWarn(ex, "读取 AppModelUnlock 注册表失败")
            Info.Status = BedrockSideloadStatus.Unknown
            Return Info
        End Try
    End Function

    ''' <summary>
    ''' 读取侧载 / 开发者模式状态。
    ''' <para>
    ''' 数据源：<c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock</c>。
    ''' AllowDevelopmentWithoutDevLicense=1 表示开发者模式，
    ''' AllowAllTrustedApps=1 表示允许侧载受信任应用。
    ''' </para>
    ''' <para>
    ''' <b>本函数只做检测，不做任何提权或修改</b>：是否开启由用户与系统策略决定。
    ''' </para>
    ''' </summary>
    Public Function GetSideloadStatus() As BedrockSideloadStatus
        Return GetSideloadInfo().Status
    End Function

    ''' <summary>注册表原始值转可读文本，便于日志与排查。</summary>
    Private Function RawToText(Raw As Object) As String
        If Raw Is Nothing Then Return "<absent>"
        Try
            If TypeOf Raw Is Integer Then Return CInt(Raw).ToString()
            Return Raw.ToString()
        Catch
            Return "<unreadable>"
        End Try
    End Function

    ''' <summary>把注册表值判断为布尔开关。缺失、0、无法解析都当作关闭。</summary>
    Private Function RawIsEnabled(Raw As Object) As Boolean
        If Raw Is Nothing Then Return False
        Try
            If TypeOf Raw Is Integer Then Return CInt(Raw) <> 0
            If TypeOf Raw Is Long Then Return CLng(Raw) <> 0
            Dim Text As String = Raw.ToString().Trim()
            Dim N As Integer
            If Integer.TryParse(Text, N) Then Return N <> 0
            Return String.Equals(Text, "true", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(Text, "yes", StringComparison.OrdinalIgnoreCase)
        Catch
            Return False
        End Try
    End Function

#End Region

#Region "唤起：协议（方案 A）"

    ''' <summary>
    ''' 唤起方案 A：用 ShellExecute 打开 minecraft://。
    ''' <para>
    ''' 交给微软统一启动器接管，<b>无法锁定版本</b>（多版本共存时由系统决定）。
    ''' 未注册协议时直接返回 False，不会弹出 Windows 的「没有关联应用」对话框。
    ''' </para>
    ''' </summary>
    Public Function LaunchViaProtocol() As Boolean
        Return LaunchViaProtocolDetailed().Success
    End Function

    ''' <summary><see cref="LaunchViaProtocol"/> 的详细版本，带失败原因。</summary>
    Public Function LaunchViaProtocolDetailed() As LaunchResult
        Dim R As New LaunchResult With {.Mode = "protocol"}
        Try
            If Not IsProtocolAvailable() Then
                R.Message = "minecraft:// 协议未注册：注册表与已安装包的清单中都没有该协议处理程序"
                Return R
            End If

            Dim Psi As New Diagnostics.ProcessStartInfo With {
                .FileName = ProtocolUri,
                .UseShellExecute = True
            }
            Diagnostics.Process.Start(Psi)

            R.Success = True
            R.Message = "已通过 minecraft:// 协议唤起，由微软统一启动器接管（无法锁定版本）"
            LogInfo(R.Message)
            Return R
        Catch ex As Exception
            R.Message = $"协议唤起失败：{ex.Message}"
            LogWarn(ex, "协议唤起失败")
            Return R
        End Try
    End Function

#End Region

#Region "唤起：UWP 激活（方案 B）"

    ''' <summary>
    ''' 唤起方案 B：用 IApplicationActivationManager 激活指定包的 !App 入口。
    ''' <para>
    ''' 相比协议唤起，这条路径<b>能锁定具体版本</b>（AUMID 精确到包家族名）。
    ''' </para>
    ''' </summary>
    ''' <param name="Pkg">要激活的基岩版包；传 Nothing 会安全返回 False。</param>
    Public Function LaunchViaActivation(Pkg As BedrockPackage) As Boolean
        Return LaunchViaActivationDetailed(Pkg).Success
    End Function

    ''' <summary>
    ''' 创建 IApplicationActivationManager 的 COM 实例。系统不支持时返回 Nothing。
    ''' </summary>
    Private Function CreateActivationManager() As IApplicationActivationManager
        Try
            Dim Co As New ApplicationActivationManager()
            Return DirectCast(Co, IApplicationActivationManager)
        Catch ex As Exception
            LogWarn(ex, "创建 IApplicationActivationManager 失败")
            Return Nothing
        End Try
    End Function

    ''' <summary><see cref="LaunchViaActivation"/> 的详细版本，带 HRESULT 与中文解释。</summary>
    Public Function LaunchViaActivationDetailed(Pkg As BedrockPackage) As LaunchResult
        Dim R As New LaunchResult With {.Mode = "activation"}

        '必须在进入 COM 之前拦住空参数：
        'AUMID 为 NULL 时 native 侧会访问冲突（0xC0000005），进程直接消失且无法 Catch
        If Pkg Is Nothing Then
            R.Message = "没有指定要激活的基岩版包"
            Return R
        End If
        If String.IsNullOrWhiteSpace(Pkg.PackageFamilyName) Then
            R.Message = "包家族名为空，无法组装 AUMID"
            Return R
        End If
        Dim Aumid As String = Pkg.AppUserModelId
        If String.IsNullOrWhiteSpace(Aumid) Then
            R.Message = "无法组装 AUMID"
            Return R
        End If

        Dim Iface As IApplicationActivationManager = CreateActivationManager()
        If Iface Is Nothing Then
            R.Message = "无法创建 IApplicationActivationManager：系统 UWP 激活组件不可用"
            R.ErrorCode = &H80040154
            Return R
        End If

        Try
            Dim Pid As UInteger = 0
            Dim Hr As Integer = Iface.ActivateApplication(Aumid, "", 0, Pid)
            R.ErrorCode = Hr
            If Hr = 0 Then
                R.Success = True
                R.ProcessId = Pid
                R.Message = $"已通过 UWP 激活启动 {Pkg.DisplayText}（PID {Pid}）"
                LogInfo(R.Message)
            Else
                R.Message = $"UWP 激活失败：{DescribeHResult(Hr)}"
                LogWarn($"{R.Message} AUMID={Aumid}")
            End If
            Return R
        Catch ex As Exception
            R.ErrorCode = ex.HResult
            R.Message = $"UWP 激活异常：{DescribeHResult(ex.HResult)}"
            LogWarn(ex, "UWP 激活异常")
            Return R
        End Try
    End Function

    ''' <summary>把常见 HRESULT 翻译成可操作的中文说明。</summary>
    Private Function DescribeHResult(Hr As Integer) As String
        Dim Code As String = "0x" & Hr.ToString("X8")
        Select Case Hr
            Case &H80073CF1
                Return $"找不到包（{Code}）：该基岩版未安装，或包家族名不匹配"
            Case &H80070057
                Return $"参数无效（{Code}）"
            Case &H80070005
                Return $"拒绝访问（{Code}）：请检查包是否为其他用户安装"
            Case &H80040154
                Return $"COM 类未注册（{Code}）：系统不支持 UWP 激活"
            Case &H80070490
                Return $"找不到元素（{Code}）"
            Case &H8007007E
                Return $"找不到模块（{Code}）"
            Case &H80004005
                Return $"未指定的错误（{Code}）"
            Case Else
                Return $"激活失败（{Code}）"
        End Select
    End Function

#End Region

#Region "唤起：第三方兜底"

    ''' <summary>
    ''' 兜底唤起方式：<c>explorer.exe shell:appsFolder\&lt;family&gt;!App</c>。
    ''' <para>
    ''' 这是 UWP 应用的标准启动入口，但 explorer 对不存在的目标也返回成功，
    ''' 所以它只作为最后一道兜底，成功不代表窗口真的起来了。
    ''' </para>
    ''' </summary>
    Public Function LaunchViaAppsFolder(Pkg As BedrockPackage) As Boolean
        Return LaunchViaAppsFolderDetailed(Pkg).Success
    End Function

    ''' <summary><see cref="LaunchViaAppsFolder"/> 的详细版本。</summary>
    Public Function LaunchViaAppsFolderDetailed(Pkg As BedrockPackage) As LaunchResult
        Dim R As New LaunchResult With {.Mode = "appsFolder"}
        If Pkg Is Nothing OrElse String.IsNullOrWhiteSpace(Pkg.PackageFamilyName) Then
            R.Message = "包家族名为空，无法通过 shell:appsFolder 唤起"
            Return R
        End If
        Try
            Dim Target As String = $"shell:appsFolder\{Pkg.PackageFamilyName}!{AppEntryId}"
            Dim Psi As New Diagnostics.ProcessStartInfo With {
                .FileName = "explorer.exe",
                .Arguments = Target,
                .UseShellExecute = True
            }
            Diagnostics.Process.Start(Psi)
            R.Success = True
            R.Message = $"已通过 shell:appsFolder 唤起（explorer 不校验目标，成功仅代表命令已发出）"
            LogInfo($"shell:appsFolder 唤起：{Target}")
            Return R
        Catch ex As Exception
            R.Message = $"shell:appsFolder 唤起失败：{ex.Message}"
            LogWarn(ex, "shell:appsFolder 唤起失败")
            Return R
        End Try
    End Function

#End Region

#Region "组合启动"

    ''' <summary>
    ''' 启动一个基岩版实例。
    ''' <para>
    ''' 按顺序尝试，任意一步成功即返回：UWP 激活（能锁定版本）→ 协议唤起 → shell:appsFolder。
    ''' <paramref name="PreferActivation"/> 为 False 时把协议唤起提到第一位。
    ''' </para>
    ''' </summary>
    ''' <param name="Pkg">目标实例；Nothing 时返回失败结果而不是抛异常。</param>
    ''' <param name="PreferActivation">是否优先走能锁定版本的 UWP 激活。</param>
    Public Function Launch(Pkg As BedrockPackage, Optional PreferActivation As Boolean = True) As LaunchResult
        If Pkg Is Nothing Then
            Return New LaunchResult With {.Message = "没有可启动的基岩版"}
        End If

        Dim Order As New List(Of String)
        If PreferActivation Then
            Order.Add("activation")
            Order.Add("protocol")
            Order.Add("appsFolder")
        Else
            Order.Add("protocol")
            Order.Add("activation")
            Order.Add("appsFolder")
        End If

        Dim Failures As New List(Of LaunchResult)
        For Each StepName As String In Order
            Dim R As LaunchResult = Nothing
            Try
                Select Case StepName
                    Case "activation"
                        R = LaunchViaActivationDetailed(Pkg)
                    Case "protocol"
                        R = LaunchViaProtocolDetailed()
                    Case Else
                        R = LaunchViaAppsFolderDetailed(Pkg)
                End Select
            Catch ex As Exception
                R = New LaunchResult With {.Mode = StepName, .Message = ex.Message}
                LogWarn(ex, $"启动步骤 {StepName} 抛出未捕获异常")
            End Try

            If R IsNot Nothing AndAlso R.Success Then Return R
            If R IsNot Nothing Then Failures.Add(R)
        Next

        Dim Detail As New Text.StringBuilder()
        Detail.Append($"所有唤起方式都失败（{Pkg.DisplayText}）。")
        For Each F In Failures
            Detail.AppendLine()
            Detail.Append($"· {F.Mode}：{F.Message}")
        Next
        Dim Result As New LaunchResult With {
            .Success = False,
            .Message = Detail.ToString(),
            .Mode = ""
        }
        If Failures.Count > 0 Then Result.ErrorCode = Failures(Failures.Count - 1).ErrorCode
        LogWarn(Result.Message)
        Return Result
    End Function

#End Region

#Region "环境摘要"

    ''' <summary>
    ''' 生成一份便于排查的环境摘要文本（协议、包、侧载三块）。
    ''' 全部内容都来自安全探测，可放心在任意机器上调用。
    ''' </summary>
    Public Function DescribeEnvironment() As String
        Dim Sb As New Text.StringBuilder()
        Try
            Sb.AppendLine($"系统：{Environment.OSVersion.VersionString}")
            Sb.AppendLine($"minecraft:// 协议（注册表）：{If(IsProtocolRegistered(), "已注册", "未注册")}")
            Dim Cmd As String = GetProtocolCommand()
            If Not String.IsNullOrWhiteSpace(Cmd) Then Sb.AppendLine($"  命令行：{Cmd}")

            Dim Pkgs = GetInstalledPackages()
            Sb.AppendLine($"已安装基岩版：{Pkgs.Count} 个")
            For Each Pkg In Pkgs
                Sb.AppendLine($"  · {Pkg.DisplayText}")
                Sb.AppendLine($"    家族名：{Pkg.PackageFamilyName}")
                Sb.AppendLine($"    全家名：{Pkg.PackageFullName}")
                Sb.AppendLine($"    目录：{Pkg.InstalledLocation}")
                Sb.AppendLine($"    开发模式：{Pkg.IsDevelopmentMode}  签名：{Pkg.SignatureKind}  清单声明协议：{HasAppxProtocolHandler(Pkg)}")
            Next

            Dim Side = GetSideloadInfo()
            Sb.AppendLine($"侧载状态：{Side.Text}（{Side.Status}）")
            Sb.AppendLine($"  注册表可读：{Side.KeyReadable}")
            Sb.AppendLine($"  {ValueDeveloperMode}={Side.RawDeveloperMode}")
            Sb.AppendLine($"  {ValueAllowAllTrustedApps}={Side.RawSideload}")
        Catch ex As Exception
            Sb.AppendLine($"环境摘要生成失败：{ex.Message}")
        End Try
        Return Sb.ToString()
    End Function

#End Region

#Region "日志（全部包异常，保证核心逻辑不因日志失败而中断）"

    Private Sub LogInfo(Message As String)
        Try
            Logger.Info(Message)
        Catch
        End Try
    End Sub

    Private Sub LogWarn(Message As String)
        Try
            Logger.Warn(Message)
        Catch
        End Try
    End Sub

    Private Sub LogWarn(Ex As Exception, Message As String)
        Try
            Logger.Warn(Ex, Message)
        Catch
        End Try
    End Sub

#End Region

End Module
