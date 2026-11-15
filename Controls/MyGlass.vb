Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Runtime.CompilerServices
Imports System.Windows.Shapes
Imports System.Windows.Media.Effects

''' <summary>
''' 毛玻璃材质工具。
''' <para>
''' WPF 没有 backdrop-filter。这里用「预渲染模糊 + 绝对 Viewbox 对齐 + 色调叠加」模拟：
''' 背景图是静态的，所以模糊只算一次并缓存，之后仅改 ImageBrush 的 Viewbox 让模糊图跟着
''' 目标元素走，不触发任何重新模糊。窗口缩放、侧边栏展开都不会掉帧。
''' </para>
''' <para>
''' 材质重量表达层级（第 12 节）：侧边栏重材质 blur 24、底色偏实；
''' 标题栏所在层背后没有可取样的内容，因此不做假模糊，改用受光边 + 渐变收口；
''' 卡片数量多，逐张真模糊会压垮渲染线程，只用半透明底 + 高光边近似。
''' </para>
''' </summary>
Public Module MyGlass

#Region "模糊位图：预渲染 + 缓存"

    ''' <summary>
    ''' 一张预渲染好的模糊图，附带留边与缩放比例。
    ''' <para>
    ''' 留边：BlurEffect 会把图像边缘向外吃掉，不留边四周会发暗。
    ''' 缩放：超大图在模糊后看不出细节，先降采样再模糊，省掉大量像素工作。
    ''' </para>
    ''' </summary>
    Public Class GlassImage
        ''' <summary>模糊后的位图，已 Freeze。</summary>
        Public Bitmap As BitmapSource
        ''' <summary>源图坐标下的留边（像素）。</summary>
        Public Pad As Double
        ''' <summary>源图坐标到模糊位图坐标的缩放比例。</summary>
        Public Scale As Double
    End Class

    Private ReadOnly BlurCache As New Dictionary(Of String, GlassImage)
    Private ReadOnly CacheLock As New Object
    Private Const MaxCacheCount As Integer = 8

    ''' <summary>
    ''' 模糊位图的最长边上限。
    ''' <para>
    ''' 模糊本身就是把细节抹掉，按原图分辨率算纯属浪费：1920x1080 背景在屏幕上只占大约 850 像素宽，
    ''' 按 900 上限先降采样再模糊，视觉上看不出差别，像素工作量却少一个数量级。
    ''' 取样时 Viewbox 会按 <see cref="GlassImage.Scale"/> 换算回去，所以对齐依然精确。
    ''' </para>
    ''' </summary>
    Private Const MaxBlurSide As Double = 900

    ''' <summary>
    ''' 从一张图生成模糊版本。同一张图 + 同一半径只计算一次。
    ''' </summary>
    Public Function BlurImage(Source As BitmapSource, Radius As Double) As BitmapSource
        Dim GI = PrepareGlass(Source, Radius)
        If GI Is Nothing Then Return Source
        Return GI.Bitmap
    End Function

    ''' <summary>
    ''' 准备模糊位图，带缓存。半径以源图像素为单位。
    ''' </summary>
    Public Function PrepareGlass(Source As BitmapSource, Radius As Double) As GlassImage
        If Source Is Nothing Then Return Nothing
        Try
            Dim R As Integer = Math.Max(1, CInt(Math.Round(Radius)))
            Dim Key As String =
                RuntimeHelpers.GetHashCode(Source).ToString & "|" &
                Source.PixelWidth.ToString & "x" & Source.PixelHeight.ToString & "|" & R.ToString

            SyncLock CacheLock
                Dim Hit As GlassImage = Nothing
                If BlurCache.TryGetValue(Key, Hit) Then Return Hit
            End SyncLock

            Dim Built As GlassImage = RenderBlur(Source, R)
            If Built Is Nothing Then Return Nothing

            SyncLock CacheLock
                If BlurCache.Count >= MaxCacheCount Then BlurCache.Clear()
                BlurCache(Key) = Built
            End SyncLock
            Return Built
        Catch ex As Exception
            Logger.Warn(ex, "准备模糊背景失败")
            Return Nothing
        End Try
    End Function

    Private Function RenderBlur(Source As BitmapSource, Radius As Integer) As GlassImage
        Try
            Dim Pad As Double = Math.Min(400, Math.Ceiling(Radius * 1.5))
            Dim FullW As Double = Source.PixelWidth + Pad * 2
            Dim FullH As Double = Source.PixelHeight + Pad * 2
            If FullW <= 0 OrElse FullH <= 0 Then Return Nothing

            Dim S As Double = Math.Min(1.0, MaxBlurSide / Math.Max(FullW, FullH))
            Dim BitmapW As Integer = Math.Max(1, CInt(Math.Round(FullW * S)))
            Dim BitmapH As Integer = Math.Max(1, CInt(Math.Round(FullH * S)))
            Dim ScaledRadius As Double = Math.Max(0.5, Radius * S)

            Dim DV As New DrawingVisual()
            Using DC As DrawingContext = DV.RenderOpen()
                DC.DrawImage(Source, New Rect(Pad * S, Pad * S,
                                              Source.PixelWidth * S, Source.PixelHeight * S))
            End Using
            DV.Effect = New BlurEffect With {
                .Radius = ScaledRadius,
                .KernelType = KernelType.Gaussian,
                .RenderingBias = RenderingBias.Performance
            }

            Dim RTB As New RenderTargetBitmap(BitmapW, BitmapH, 96, 96, PixelFormats.Pbgra32)
            RTB.Render(DV)
            RTB.Freeze()

            Return New GlassImage With {.Bitmap = RTB, .Pad = Pad, .Scale = S}
        Catch ex As Exception
            Logger.Warn(ex, "渲染模糊背景失败")
            Return Nothing
        End Try
    End Function

#End Region

#Region "色调与高光"

    ''' <summary>
    ''' 生成一层色调。颜色自身的 Alpha 会被 Opacity 缩放。
    ''' </summary>
    Public Function TintBrush(TintColor As Color, TintOpacity As Double) As SolidColorBrush
        Dim T As Double = Math.Max(0, Math.Min(1, TintOpacity))
        Dim Alpha As Integer = CInt(Math.Round(TintColor.A * T))
        Alpha = Math.Max(0, Math.Min(255, Alpha))
        Dim B As New SolidColorBrush(Color.FromArgb(CByte(Alpha), TintColor.R, TintColor.G, TintColor.B))
        B.Freeze()
        Return B
    End Function

    ''' <summary>
    ''' 自上而下由亮到透明的高光边：光打在材质顶面上的那一下。
    ''' </summary>
    Public Function HighlightRim(TopAlpha As Byte, BottomAlpha As Byte) As LinearGradientBrush
        Dim G As New LinearGradientBrush With {
            .StartPoint = New Point(0.5, 0),
            .EndPoint = New Point(0.5, 1)
        }
        G.GradientStops.Add(New GradientStop(Color.FromArgb(TopAlpha, 255, 255, 255), 0))
        G.GradientStops.Add(New GradientStop(Color.FromArgb(BottomAlpha, 255, 255, 255), 1))
        G.Freeze()
        Return G
    End Function

    ''' <summary>
    ''' 玻璃边框：圆角 + 受光高光边。
    ''' </summary>
    Public Sub ApplyGlassBorder(Target As Border, Radius As Double)
        If Target Is Nothing Then Return
        Try
            Target.CornerRadius = New CornerRadius(Radius)
            Target.BorderThickness = New Thickness(1)
            Target.BorderBrush = HighlightRim(140, 0)
        Catch ex As Exception
            Logger.Warn(ex, "应用毛玻璃边框失败")
        End Try
    End Sub

    ''' <summary>
    ''' 给一个 Panel 的底层插入纯色调（没有背景源时使用）。
    ''' </summary>
    Public Sub ApplyGlass(Target As FrameworkElement, Radius As Double, TintColor As Color, TintOpacity As Double)
        If Target Is Nothing Then Return
        Try
            Dim Layer = TryCast(Target, Panel)
            If Layer Is Nothing Then Return
            Layer.Children.Insert(0, New Rectangle With {
                .IsHitTestVisible = False,
                .RadiusX = Radius,
                .RadiusY = Radius,
                .Fill = TintBrush(TintColor, TintOpacity)
            })
        Catch ex As Exception
            Logger.Warn(ex, "应用毛玻璃失败")
        End Try
    End Sub

#End Region

#Region "背景毛玻璃"

    ' 已挂过尺寸监听的元素，避免重复订阅导致回调越积越多
    Private ReadOnly Attached As New HashSet(Of FrameworkElement)
    Private ReadOnly AttachLock As New Object

    Private Function AttachOnce(E As FrameworkElement) As Boolean
        SyncLock AttachLock
            If Attached.Contains(E) Then Return False
            Attached.Add(E)
            Return True
        End SyncLock
    End Function

    ''' <summary>
    ''' 让一块矩形真正"透过"背景：底层贴预渲染的模糊背景切片，上层叠色调。
    ''' <para>
    ''' 尺寸变化时只重算 Viewbox，不重新模糊。BlurRadius 指的是屏幕像素半径。
    ''' </para>
    ''' </summary>
    ''' <param name="Backdrop">背景源元素，通常是窗口里承载背景图的 Canvas。</param>
    ''' <param name="SourceBrush">Backdrop 使用的 ImageBrush，用于取原图。</param>
    ''' <param name="BlurLayer">承载模糊切片的矩形。</param>
    ''' <param name="TintLayer">承载色调的矩形，可为 Nothing。</param>
    Public Sub ApplyBackdropGlass(Backdrop As FrameworkElement, SourceBrush As ImageBrush,
                                  BlurLayer As Rectangle, TintLayer As Rectangle,
                                  BlurRadius As Double, TintColor As Color, TintOpacity As Double)
        If Backdrop Is Nothing OrElse BlurLayer Is Nothing Then Return
        Try
            If TintLayer IsNot Nothing Then TintLayer.Fill = TintBrush(TintColor, TintOpacity)

            Dim Refresh As Action =
                Sub()
                    Try
                        RefreshGlassSlice(Backdrop, SourceBrush, BlurLayer, BlurRadius)
                    Catch ex As Exception
                        Logger.Warn(ex, "更新毛玻璃对齐失败")
                    End Try
                End Sub

            Refresh()
            'VB 里不能写 `X.SizeChanged += handler`：那会被解析成调用事件本身（BC32022）。
            '必须用 AddHandler 关键字。
            If AttachOnce(Backdrop) Then AddHandler Backdrop.SizeChanged, Sub(s, e) Refresh()
            If AttachOnce(BlurLayer) Then AddHandler BlurLayer.SizeChanged, Sub(s, e) Refresh()
        Catch ex As Exception
            Logger.Warn(ex, "应用背景毛玻璃失败")
        End Try
    End Sub

    ''' <summary>
    ''' 重算模糊切片在目标矩形上的取样区域。
    ''' <para>
    ''' 背景按 UniformToFill 居中铺在 Backdrop 上，源图像素与 Backdrop 坐标之间是一个
    ''' 等比缩放加居中偏移。求出目标矩形左上角落在源图的哪个像素，再换算到模糊位图坐标即可。
    ''' </para>
    ''' </summary>
    Private Sub RefreshGlassSlice(Backdrop As FrameworkElement, SourceBrush As ImageBrush,
                                  BlurLayer As Rectangle, BlurRadius As Double)
        If Backdrop Is Nothing OrElse BlurLayer Is Nothing Then Return

        Dim W As Double = Backdrop.ActualWidth
        Dim H As Double = Backdrop.ActualHeight
        Dim TW As Double = BlurLayer.ActualWidth
        Dim TH As Double = BlurLayer.ActualHeight
        If W <= 0 OrElse H <= 0 OrElse TW <= 0 OrElse TH <= 0 Then Return

        Dim Src As BitmapSource = Nothing
        If SourceBrush IsNot Nothing Then Src = TryCast(SourceBrush.ImageSource, BitmapSource)
        If Src Is Nothing OrElse Src.PixelWidth <= 0 OrElse Src.PixelHeight <= 0 Then
            BlurLayer.Visibility = Visibility.Collapsed
            Return
        End If

        ' 只保证最常见的"居中 UniformToFill 不平铺"能精确对齐。
        ' 其他适应方式算下去只会更难看，直接退回不做真模糊，让上层的色调单独成立。
        Dim Aligned As Boolean =
            SourceBrush.Stretch = Stretch.UniformToFill AndAlso
            SourceBrush.AlignmentX = AlignmentX.Center AndAlso
            SourceBrush.AlignmentY = AlignmentY.Center AndAlso
            SourceBrush.TileMode = TileMode.None
        If Not Aligned Then
            BlurLayer.Visibility = Visibility.Collapsed
            Return
        End If
        BlurLayer.Visibility = Visibility.Visible

        Dim S As Double = Math.Max(W / Src.PixelWidth, H / Src.PixelHeight)
        If S <= 0 Then Return
        Dim OffsetX As Double = (W - Src.PixelWidth * S) / 2
        Dim OffsetY As Double = (H - Src.PixelHeight * S) / 2

        Dim Origin As Point
        Try
            Origin = BlurLayer.TranslatePoint(New Point(0, 0), Backdrop)
        Catch
            Return
        End Try

        Dim SrcX As Double = (Origin.X - OffsetX) / S
        Dim SrcY As Double = (Origin.Y - OffsetY) / S
        Dim SrcW As Double = TW / S
        Dim SrcH As Double = TH / S

        ' 屏幕像素半径换算到源图分辨率
        Dim GI = PrepareGlass(Src, BlurRadius / S)
        If GI Is Nothing Then Return

        Dim B As New ImageBrush(GI.Bitmap) With {
            .Stretch = Stretch.Fill,
            .AlignmentX = AlignmentX.Left,
            .AlignmentY = AlignmentY.Top,
            .ViewboxUnits = BrushMappingMode.Absolute,
            .ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            .Viewport = New Rect(0, 0, 1, 1),
            .Viewbox = New Rect((SrcX + GI.Pad) * GI.Scale,
                                (SrcY + GI.Pad) * GI.Scale,
                                SrcW * GI.Scale,
                                SrcH * GI.Scale)
        }
        B.Freeze()
        BlurLayer.Fill = B
    End Sub

#End Region

#Region "卡片材质"

    ''' <summary>
    ''' 卡片轻材质：半透明白底 + 顶部高光边。
    ''' <para>
    ''' 卡片数量多，逐张真模糊会把渲染线程压垮，所以只做"像玻璃"的近似。
    ''' 白底带 224/255 的不透明度：足够透出背景的色，又不至于让正文失焦。
    ''' </para>
    ''' </summary>
    Public Sub ApplyCardSurface(Target As Border)
        If Target Is Nothing Then Return
        Try
            Target.Background = New SolidColorBrush(Color.FromArgb(224, 255, 255, 255))
            Target.BorderThickness = New Thickness(1)
            Target.BorderBrush = HighlightRim(140, 0)
        Catch ex As Exception
            Logger.Warn(ex, "应用卡片材质失败")
        End Try
    End Sub

#End Region

#Region "Materialize"

    ''' <summary>
    ''' 玻璃表面出现时，缩放与不透明度一起动，读作"材质浮上来"，而不只是淡入。
    ''' </summary>
    Public Sub Materialize(Target As FrameworkElement, Optional Duration As Integer = 220, Optional FromScale As Double = 0.985)
        If Target Is Nothing Then Return
        Try
            Target.RenderTransformOrigin = New Point(0.5, 0.5)
            Target.RenderTransform = New ScaleTransform(FromScale, FromScale)
            Target.Opacity = 0
            AniStart({
                AaScaleTransform(Target, 1 - FromScale, Duration,, New AniEaseOutFluent(AniEasePower.Weak)),
                AaOpacity(Target, 1, Duration,, New AniEaseOutFluent(AniEasePower.Weak))
            }, "Glass Materialize " & RuntimeHelpers.GetHashCode(Target).ToString)
        Catch ex As Exception
            Logger.Warn(ex, "毛玻璃进入动画失败")
            Target.Opacity = 1
        End Try
    End Sub

#End Region

#Region "本机地址探测"

    ''' <summary>
    ''' 判断一个 IPv4 是否属于内网/保留段。
    ''' <para>
    ''' 覆盖 10/8、172.16/12、192.168/16、127/8、169.254/16 自动配置段，
    ''' 以及 100.64/10 运营商级 NAT。
    ''' </para>
    ''' </summary>
    Public Function IsPrivateIPv4(Address As String) As Boolean
        If String.IsNullOrEmpty(Address) Then Return True
        Dim Parts = Address.Split("."c)
        If Parts.Length <> 4 Then Return True
        Dim A As Integer, B As Integer
        If Not Integer.TryParse(Parts(0), A) Then Return True
        If Not Integer.TryParse(Parts(1), B) Then Return True
        If A = 10 Then Return True
        If A = 172 AndAlso B >= 16 AndAlso B <= 31 Then Return True
        If A = 192 AndAlso B = 168 Then Return True
        If A = 127 Then Return True
        If A = 169 AndAlso B = 254 Then Return True
        If A = 100 AndAlso B >= 64 AndAlso B <= 127 Then Return True
        Return False
    End Function

    ''' <summary>
    ''' 取本机第一张可用网卡的 IPv4 地址，取不到返回空串。
    ''' </summary>
    Public Function DetectLocalIPv4() As String
        Try
            For Each NI As NetworkInterface In NetworkInterface.GetAllNetworkInterfaces()
                If NI.OperationalStatus <> OperationalStatus.Up Then Continue For
                If NI.NetworkInterfaceType = NetworkInterfaceType.Loopback Then Continue For
                If NI.NetworkInterfaceType = NetworkInterfaceType.Tunnel Then Continue For
                For Each UA As UnicastIPAddressInformation In NI.GetIPProperties().UnicastAddresses
                    If UA.Address.AddressFamily <> AddressFamily.InterNetwork Then Continue For
                    Dim S As String = UA.Address.ToString()
                    If S.StartsWith("169.254.") Then Continue For
                    Return S
                Next
            Next
        Catch ex As Exception
            Logger.Warn(ex, "检测本机 IP 失败")
        End Try
        Return ""
    End Function

#End Region

End Module

''' <summary>
''' 供 XAML 直接取用的本机网络信息。
''' <para>
''' VB 的 Module 不能在 XAML 里当类型引用，所以单独放一个类。
''' 值在类型初始化时算一次，页面重建不会重复探测网卡。
''' </para>
''' </summary>
Public NotInheritable Class GlassNetInfo

    Private Sub New()
    End Sub

    ''' <summary>本机第一张可用网卡的 IPv4，取不到为空串。</summary>
    Public Shared ReadOnly LocalIPv4 As String = MyGlass.DetectLocalIPv4()

    ''' <summary>本机 IPv4 是否属于内网/保留段。</summary>
    Public Shared ReadOnly IsPrivate As Boolean = MyGlass.IsPrivateIPv4(LocalIPv4)

    ''' <summary>一句话结论。</summary>
    Public Shared ReadOnly AddressHint As String = BuildHint()

    ''' <summary>给用户看的本机地址，取不到时给出可读替代。</summary>
    Public Shared ReadOnly LocalAddressText As String = If(String.IsNullOrEmpty(LocalIPv4), "未检测到", LocalIPv4)

    Private Shared Function BuildHint() As String
        Try
            If String.IsNullOrEmpty(LocalIPv4) Then Return "没有检测到可用的本机 IPv4 地址。"
            If IsPrivate Then
                Return "当前是内网 IP（" & LocalIPv4 & "），外网无法直接连进来，需要隧道或端口映射。"
            End If
            Return "当前是公网 IP（" & LocalIPv4 & "），在路由器放行端口即可让朋友连进来。"
        Catch ex As Exception
            Return "本机地址检测失败，请手动查看网络设置。"
        End Try
    End Function

End Class
