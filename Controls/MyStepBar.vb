Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Shapes

''' <summary>
''' 开服向导的步骤条：把「选类型 → 选核心 → 选版本 → 启动」画成一条可视进度。
''' <para>
''' 依据 spt「先定状态，再做动画」：步骤条本身就是状态显示，不是装饰——
''' 玩家随时知道自己在第几步、还剩几步。配色全部走 DynamicResource，
''' 跟随主题切换，不硬编码颜色。
''' </para>
''' </summary>
Public Class MyStepBar
    Inherits Grid

    Private ReadOnly StepLabels As String() = {"选类型", "选核心", "选版本", "启动"}
    Private _Current As Integer = 0
    Private ReadOnly Circles As New List(Of Border)
    Private ReadOnly Labels As New List(Of TextBlock)
    Private ReadOnly Lines As New List(Of Rectangle)

    Public Sub New()
        Build()
    End Sub

    ''' <summary>当前步骤（0 起）。</summary>
    Public Property Current As Integer
        Get
            Return _Current
        End Get
        Set(value As Integer)
            Dim v = Math.Max(0, Math.Min(StepLabels.Length - 1, value))
            If v = _Current Then Return
            _Current = v
            RefreshState(True)
        End Set
    End Property

    Private Sub Build()
        Try
            ColumnDefinitions.Clear()
            RowDefinitions.Clear()
            RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(28)})
            RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})

            For i = 0 To StepLabels.Length - 1
                If i > 0 Then ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
                ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Next

            For i = 0 To StepLabels.Length - 1
                Dim col = i * 2

                Dim Dot As New Border With {
                    .Width = 24, .Height = 24,
                    .CornerRadius = New CornerRadius(12),
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .VerticalAlignment = VerticalAlignment.Center,
                    .BorderThickness = New Thickness(2)
                }
                Dim Num As New TextBlock With {
                    .Text = (i + 1).ToString,
                    .FontSize = 12,
                    .FontWeight = FontWeights.SemiBold,
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .VerticalAlignment = VerticalAlignment.Center
                }
                Dot.Child = Num
                SetRow(Dot, 0)
                SetColumn(Dot, col)
                Children.Add(Dot)
                Circles.Add(Dot)

                Dim Lab As New TextBlock With {
                    .Text = StepLabels(i),
                    .FontSize = 12,
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .Margin = New Thickness(0, 2, 0, 0)
                }
                SetRow(Lab, 1)
                SetColumn(Lab, col)
                Children.Add(Lab)
                Labels.Add(Lab)

                If i < StepLabels.Length - 1 Then
                    Dim Ln As New Rectangle With {
                        .Height = 2,
                        .VerticalAlignment = VerticalAlignment.Center,
                        .Margin = New Thickness(4, 0, 4, 0),
                        .RadiusX = 1, .RadiusY = 1
                    }
                    SetRow(Ln, 0)
                    SetColumn(Ln, col + 1)
                    Children.Add(Ln)
                    Lines.Add(Ln)
                End If
            Next

            RefreshState(False)
        Catch ex As Exception
            Logger.Warn(ex, "构建步骤条失败")
        End Try
    End Sub

    Private Sub RefreshState(Animate As Boolean)
        Try
            For i = 0 To Circles.Count - 1
                Dim IsDone = i < _Current
                Dim IsNow = i = _Current
                Dim Dot = Circles(i)
                Dim Lab = Labels(i)

                '主题色与灰色都走资源，跟随主题切换
                If IsDone Then
                    Dot.SetResourceReference(Border.BackgroundProperty, "ColorBrush2")
                    Dot.SetResourceReference(Border.BorderBrushProperty, "ColorBrush2")
                    CType(Dot.Child, TextBlock).Foreground = Brushes.White
                ElseIf IsNow Then
                    Dot.Background = Brushes.White
                    Dot.SetResourceReference(Border.BorderBrushProperty, "ColorBrush2")
                    CType(Dot.Child, TextBlock).SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
                Else
                    Dot.Background = Brushes.Transparent
                    Dot.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray3")
                    CType(Dot.Child, TextBlock).SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                End If

                If IsDone OrElse IsNow Then
                    Lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
                Else
                    Lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                End If
                Lab.FontWeight = If(IsNow, FontWeights.SemiBold, FontWeights.Normal)

                If i < Lines.Count Then
                    If IsDone Then
                        Lines(i).SetResourceReference(Rectangle.FillProperty, "ColorBrush2")
                    Else
                        Lines(i).SetResourceReference(Rectangle.FillProperty, "ColorBrushGray3")
                    End If
                End If

                '当前步骤轻微放大：180ms ease-out，只在切换时触发一次
                If Animate AndAlso IsNow Then
                    Dim Sc As New ScaleTransform(1, 1)
                    Dot.RenderTransformOrigin = New Point(0.5, 0.5)
                    Dot.RenderTransform = Sc
                    AniStart(AaScaleTransform(Dot, 0.15, 180, 0, New AniEaseOutFluent(AniEasePower.Weak)), "StepBarPop")
                End If
            Next
        Catch ex As Exception
            Logger.Warn(ex, "刷新步骤条失败")
        End Try
    End Sub
End Class
