''' <summary>
''' 联机板块的左侧栏：创建房间 / 加入房间 / 大厅。
''' <para>
''' 之前联机板块是「New MyPageLeft」空栏，视觉上和开服、插件不一致。
''' 这里补上真实侧边栏，让三个板块的导航形态统一。
''' </para>
''' </summary>
Public Class PageLinkLeft

    ''' <summary>当前子页面编号。0=创建 1=加入 2=大厅</summary>
    Public PageID As Integer = 0

    ''' <summary>切换子页面。</summary>
    Public Event PageChanged As EventHandler(Of Integer)

    Private Sub PageCheck(sender As FrameworkElement, e As RouteEventArgs) Handles ItemCreate.Check, ItemJoin.Check, ItemLobby.Check
        If sender.Tag Is Nothing Then Return
        PageID = CInt(Val(sender.Tag))
        RaiseEvent PageChanged(Me, PageID)
    End Sub

    ''' <summary>外部设置当前页（不会触发事件）。</summary>
    Public Sub SetPage(ID As Integer)
        PageID = ID
        Dim Items = {ItemCreate, ItemJoin, ItemLobby}
        For Each It In Items
            If CInt(Val(It.Tag)) = ID Then
                It.SetChecked(True, False, False)
                Exit For
            End If
        Next
    End Sub

End Class
