Public Class PclLogger
    Inherits FileLogger

    ''' <inheritdoc/>
    Public Overrides Function Format(Text As String, Level As LogLevel, FilePath As String, Ex As Exception) As String
        Text = MyBase.Format(Text, Level, FilePath, Ex)
        Text = FilterUserName(FilterAccessToken(Text, "*"c), "*"c)
        Return Text
    End Function

    ''' <inheritdoc/>
    Public Overrides Sub HandleBehavior(RawMessage As String, FormattedMessage As String, Behavior As LogBehavior, Ex As Exception)
        If IsProgramEnding Then Return
        MyBase.HandleBehavior(RawMessage, FormattedMessage, Behavior, Ex)
        Dim BriefText = If(Ex Is Nothing, RawMessage, If(RawMessage Is Nothing, "", $"{RawMessage}：") & Ex.GetDisplay(False))
        BriefText = FilterUserName(FilterAccessToken(BriefText, "*"c), "*"c)
        Dim DetailText = If(Ex Is Nothing, RawMessage, If(RawMessage Is Nothing, "", $"{RawMessage}：") & Ex.GetDisplay(True))
        DetailText = FilterUserName(FilterAccessToken(DetailText, "*"c), "*"c)
        Select Case Behavior
            Case LogBehavior.None
                '啥也不干
            Case LogBehavior.ToastIfDebug
                '调试模式会把所有网络警告都弹成提示条，但像 CurseForge 403、
                'OptiFine 官网 502 这类是第三方服务端的问题，用户点掉了也解决不了，
                '只会让界面一直冒蓝条。这些只写日志、不弹提示。
                If BuildType = BuildTypes.Debug OrElse ModeDebug Then
                    If Not IsNonActionableNetworkIssue(BriefText) Then
                        Hint("[调试模式] " & BriefText, HintType.Blue, False)
                    End If
                End If
            Case LogBehavior.Toast
                Hint(BriefText, HintType.Red, False)
            Case LogBehavior.Alert
                MyMsgBox(DetailText, "错误", IsWarn:=True)
            Case LogBehavior.AlertThenFeedback
                If PageSetupSystem.IsLauncherNewest Then
                    If MyMsgBox(DetailText & vbCrLf & vbCrLf & "是否反馈此问题？如果不反馈，这个问题可能永远无法得到解决！", "错误", "反馈", "取消", IsWarn:=True) = 1 Then Feedback(False, True)
                Else
                    MyMsgBox(DetailText & vbCrLf & vbCrLf & "将 PCL 更新至最新版或许可以解决这个问题……", "错误", IsWarn:=True)
                End If
            Case LogBehavior.AlertThenCrash
                Static FirstTrigger As Boolean = True
                If FirstTrigger Then
                    '首次触发
                    FirstTrigger = False
                    If PageSetupSystem.IsLauncherNewest Then
                        If MsgBox(DetailText & vbCrLf & vbCrLf & "是否反馈此问题？如果不反馈，这个问题可能永远无法得到解决！", MsgBoxStyle.Critical + MsgBoxStyle.YesNo, "错误") = MsgBoxResult.Yes Then Feedback(False, True)
                    Else
                        MsgBox(DetailText & vbCrLf & vbCrLf & "将 PCL 更新至最新版或许可以解决这个问题……", MsgBoxStyle.Critical, "错误")
                    End If
                Else
                    '多次触发，直接使程序崩溃（这通常代表着在其他线程循环触发严重异常）
                    Thread.Sleep(2000)
                End If
                FormMain.EndProgramForce(ProcessReturnValues.Exception)
        End Select
        '遥测
        If Behavior >= LogBehavior.Toast Then Telemetry("错误日志", "Exception", DetailText)
    End Sub


    ''' <summary>
    ''' 判断一条网络错误是否属于「用户改不了、弹出来也没用」的类型。
    '''
    ''' <para>典型例子：CurseForge 的 403（他们的 API 要 Key，我们调不通）、
    ''' 镜像站 502、各加载器官网临时抽风。这些第三方服务端的问题
    ''' 用户除了换个时间重试没有别的办法，反复弹提示只会干扰操作。</para>
    ''' </summary>
    Private Function IsNonActionableNetworkIssue(Text As String) As Boolean
        If String.IsNullOrWhiteSpace(Text) Then Return False

        '只在「网络请求失败」这类上生效，避免误吞真正的配置错误
        If Not (Text.Contains("网络请求失败") OrElse Text.Contains("HttpRequestCodeException")) Then Return False

        '已知的第三方接口与镜像站
        Dim ThirdParty As String() = {
            "api.curseforge.com", "curseforge.com", "api.modrinth.com",
            "mod.mcimirror.top", "optifine.net", "files.minecraftforge.net",
            "maven.neoforged.net", "bmclapi"
        }
        For Each Host In ThirdParty
            If Text.Contains(Host) Then Return True
        Next

        'HTTP 状态码类：403 / 404 / 429 / 5xx 基本都是对方服务端的问题
        If Text.Contains("Forbidden (403)") OrElse
           Text.Contains("NotFound (404)") OrElse
           Text.Contains("TooManyRequests (429)") OrElse
           Text.Contains("BadGateway (502)") OrElse
           Text.Contains("ServiceUnavailable (503)") OrElse
           Text.Contains("GatewayTimeout (504)") Then
            Return True
        End If

        Return False
    End Function

End Class
