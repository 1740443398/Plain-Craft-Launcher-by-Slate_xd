Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports System.Windows.Shapes

''' <summary>
''' 液态 Tab 指示器。
''' <para>
''' 依据 spt「Liquid Tab Indicator」：标签文字与命中区域完全不动，
''' 只有一块独立的背景在标签之间移动，移动时先拉长再收拢，末尾轻微回弹。
''' </para>
''' <para>
''' 为什么不改 MyRadioButton 本身：那会同时影响侧边栏、单选组等所有用到它的地方。
''' 这里做成叠加层，只服务顶部标签栏。
''' </para>
''' </summary>
Public Class MyLiquidIndicator
    Inherits Canvas

    Private ReadOnly Pill As Border
    Private ReadOnly PillTransform As ScaleTransform
    Private CurrentLeft As Double = 0
    Private CurrentWidth As Double = 0
    Private HasPosition As Boolean = False

    Public Sub New()
        Try
            IsHitTestVisible = False
            ClipToBounds = False
            Background = Nothing

            PillTransform = New ScaleTransform(1, 1)

            Pill = New Border With {
                .CornerRadius = New CornerRadius(13.5),
                .Height = 27,
                .MinWidth = 8,
                .VerticalAlignment = VerticalAlignment.Center,
                .RenderTransformOrigin = New Point(0.5, 0.5),
                .RenderTransform = PillTransform,
                .IsHitTestVisible = False
            }
            '主题色，跟随主题切换
            Pill.SetResourceReference(Border.BackgroundProperty, "ColorBrush2")

            '放到最底层：标签按钮本身有 #01 的半透明背景，会盖住指示器
            Panel.SetZIndex(Pill, -1)
            Children.Add(Pill)
        Catch ex As Exception
            Logger.Warn(ex, "初始化液态指示器失败")
        End Try
    End Sub

    ''' <summary>
    ''' 移动到目标位置。targetLeft/targetWidth 来自实际标签控件的坐标。
    ''' </summary>
    ''' <param name="Animate">首次定位时传 False，直接落位不做动画。</param>
    Public Sub MoveTo(TargetLeft As Double, TargetWidth As Double, Optional Animate As Boolean = True)
        Try
            If TargetWidth <= 0 Then Return

            '首次定位：直接落位，避免从 (0,0) 飞过去
            If Not HasPosition OrElse Not Animate Then
                CurrentLeft = TargetLeft
                CurrentWidth = TargetWidth
                HasPosition = True
                ApplyGeometry()
                Return
            End If

            '宽度没变、位置没变就不动，避免无意义动画
            If Math.Abs(TargetLeft - CurrentLeft) < 0.5 AndAlso Math.Abs(TargetWidth - CurrentWidth) < 0.5 Then Return

            Dim GoingRight As Boolean = TargetLeft > CurrentLeft
            Dim OldLeft = CurrentLeft
            Dim OldWidth = CurrentWidth
            Dim OldRight = OldLeft + OldWidth
            Dim NewRight = TargetLeft + TargetWidth

            '—— 阶段一：拉伸 ——
            '向右移动时，右边缘先冲出去；向左移动时，左边缘先冲出去。
            '这就是「先拉长」的来源。
            Dim StretchSpan As Double = Math.Abs(TargetLeft - CurrentLeft)
            Dim StretchExtra As Double = Math.Min(StretchSpan * 0.45, 26)
            Dim MidLeft As Double
            Dim MidWidth As Double

            If GoingRight Then
                MidLeft = OldLeft
                MidWidth = Math.Max(OldWidth, NewRight - OldLeft) + StretchExtra
            Else
                MidLeft = Math.Min(OldLeft, TargetLeft) - StretchExtra
                MidWidth = Math.Max(OldWidth, OldRight - TargetLeft) + StretchExtra
            End If

            '—— 阶段二：收拢到目标 ——
            CurrentLeft = TargetLeft
            CurrentWidth = TargetWidth

            '用两个连续动画表达「拉长 → 收拢」，总时长 260ms：
            '前 130ms 拉长（ease-out 快起），后 130ms 收拢（ease-in-out 稳落）
            Dim Story As New Storyboard

            'Left：从旧位置到中间位置，再到目标位置
            Dim LeftAnim As New DoubleAnimationUsingKeyFrames
            LeftAnim.Duration = TimeSpan.FromMilliseconds(260)
            LeftAnim.KeyFrames.Add(New EasingDoubleKeyFrame(OldLeft, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))))
            LeftAnim.KeyFrames.Add(New EasingDoubleKeyFrame(MidLeft, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)),
                                                            New CubicEase With {.EasingMode = EasingMode.EaseOut}))
            LeftAnim.KeyFrames.Add(New EasingDoubleKeyFrame(TargetLeft, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
                                                            New CubicEase With {.EasingMode = EasingMode.EaseOut}))
            Storyboard.SetTarget(LeftAnim, Pill)
            Storyboard.SetTargetProperty(LeftAnim, New PropertyPath("(Canvas.Left)"))

            'Width：同步拉伸再收拢
            Dim WidthAnim As New DoubleAnimationUsingKeyFrames
            WidthAnim.Duration = TimeSpan.FromMilliseconds(260)
            WidthAnim.KeyFrames.Add(New EasingDoubleKeyFrame(OldWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))))
            WidthAnim.KeyFrames.Add(New EasingDoubleKeyFrame(MidWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)),
                                                             New CubicEase With {.EasingMode = EasingMode.EaseOut}))
            WidthAnim.KeyFrames.Add(New EasingDoubleKeyFrame(TargetWidth, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
                                                             New BackEase With {.Amplitude = 0.12, .EasingMode = EasingMode.EaseOut}))
            Storyboard.SetTarget(WidthAnim, Pill)
            Storyboard.SetTargetProperty(WidthAnim, New PropertyPath("Width"))

            Story.Children.Add(LeftAnim)
            Story.Children.Add(WidthAnim)
            Story.Begin()
        Catch ex As Exception
            Logger.Warn(ex, "移动液态指示器失败")
        End Try
    End Sub

    Private Sub ApplyGeometry()
        Try
            SetLeft(Pill, CurrentLeft)
            Pill.Width = CurrentWidth
        Catch ex As Exception
            Logger.Warn(ex, "设置指示器几何失败")
        End Try
    End Sub
End Class
