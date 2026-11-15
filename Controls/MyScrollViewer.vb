Public Class MyScrollViewer
    Inherits ScrollViewer

    Public Property DeltaMult As Double = 1

    Private RealOffset As Double
    ''' <summary>
    ''' 是否允许把滚轮事件交给外层 ScrollViewer。
    ''' <para>
    ''' 默认 False：和原版行为一致，本控件独占滚轮。
    ''' 设为 True 后：滚到顶/底时不再吞掉事件，让外层的 MyScrollViewer 接着滚。
    ''' 嵌套场景（比如卡片里再放一个可滚动区域）必须开这个，
    ''' 否则内层滚到头之后整个界面就卡住不动了。
    ''' </para>
    ''' </summary>
    Public Property AllowBubbleToParent As Boolean = False

    Private Sub MyScrollViewer_PreviewMouseWheel(sender As Object, e As MouseWheelEventArgs) Handles Me.PreviewMouseWheel
        If e.Delta = 0 OrElse ActualHeight = 0 Then Return

        Dim SourceType = e.Source.GetType
        If Content IsNot Nothing AndAlso Content.TemplatedParent Is Nothing AndAlso (
                (GetType(ComboBox).IsAssignableFrom(SourceType) AndAlso CType(e.Source, ComboBox).IsDropDownOpen) OrElse
                (GetType(TextBox).IsAssignableFrom(SourceType) AndAlso CType(e.Source, TextBox).AcceptsReturn) OrElse
                GetType(ComboBoxItem).IsAssignableFrom(SourceType) OrElse
                (TypeOf e.Source Is Visual AndAlso
                    PresentationSource.FromVisual(e.Source)?.RootVisual?.GetType().FullName = "System.Windows.Controls.Primitives.PopupRoot")) Then
            '如果当前是在对有滚动条的下拉框或文本框执行，则不接管操作
            Return
        End If

        '—— 嵌套场景：滚到头就把事件让给外层 ——
        If AllowBubbleToParent Then
            If ScrollableHeight <= 0 Then
                '本层根本不能滚，直接放行
                Return
            End If
            Dim GoingDown As Boolean = e.Delta < 0
            Dim AtBottom As Boolean = VerticalOffset >= ScrollableHeight - 0.5
            Dim AtTop As Boolean = VerticalOffset <= 0.5
            If (GoingDown AndAlso AtBottom) OrElse (Not GoingDown AndAlso AtTop) Then
                '已经到头，不吞事件，交给外层继续滚
                Return
            End If
        Else
            If ScrollableHeight = 0 Then Return
        End If

        e.Handled = True
        PerformVerticalOffsetDelta(-e.Delta)
    End Sub
    Public Sub PerformVerticalOffsetDelta(Delta As Double)
        AniStart(
            AaDouble(
            Sub(AnimDelta As Double)
                RealOffset = (RealOffset + AnimDelta).Clamp(0, ExtentHeight - ActualHeight)
                ScrollToVerticalOffset(RealOffset)
            End Sub, Delta * DeltaMult, 300,, New AniEaseOutFluent(6)))
    End Sub
    Private Sub MyScrollViewer_ScrollChanged(sender As Object, e As ScrollChangedEventArgs) Handles Me.ScrollChanged
        RealOffset = VerticalOffset
        If FrmMain IsNot Nothing AndAlso (e.VerticalChange OrElse e.ViewportHeightChange) Then FrmMain.BtnExtraBack.ShowRefresh()
    End Sub
    Private Sub MyScrollViewer_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs) Handles Me.IsVisibleChanged
        FrmMain.BtnExtraBack.ShowRefresh()
    End Sub

    Public ScrollBar As MyScrollBar
    Private Sub Load() Handles Me.Loaded
        ScrollBar = GetTemplateChild("PART_VerticalScrollBar")
    End Sub

    Private Sub MyScrollViewer_PreviewGotKeyboardFocus(sender As Object, e As KeyboardFocusChangedEventArgs) Handles Me.PreviewGotKeyboardFocus
        If e.NewFocus IsNot Nothing AndAlso TypeOf e.NewFocus Is MySlider Then e.Handled = True '#3854，阻止获得焦点时自动滚动
    End Sub
End Class
