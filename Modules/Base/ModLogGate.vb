''' <summary>
''' 批量任务的日志节流。
'''
''' <b>要解决的问题</b>：调试模式打开时日志级别是 Trace。
''' 下载原版资源动辄几千个文件，每个文件的「写入文件」都记一条 Trace，
''' 结果是数万次同步磁盘写入，下载中途会明显卡顿甚至假死
''' （实测 4540 个文件产生 3.3 万行 Trace，日志文件 7.6 MB）。
'''
''' <b>做法</b>：批量任务开始时临时把 MinLevel 提到 Info，结束后恢复，
''' 这样正常日志照常记，只有逐文件的 Trace 被抑制掉。
''' 用引用计数支持嵌套调用（例如「下载游戏」里又包含子任务）。
''' </summary>
Public Module ModLogGate

    ''' <summary>当前嵌套层数。大于 0 表示正在批量任务中。</summary>
    Private GateDepth As Integer = 0

    ''' <summary>进入批量任务。返回的对象 Dispose 时退出。</summary>
    ''' <remarks>
    ''' 用法：<c>Using ModLogGate.Quiet()</c> ... <c>End Using</c>
    ''' </remarks>
    Public Function Quiet() As IDisposable
        SyncLock GetType(ModLogGate)
            GateDepth += 1
            If GateDepth = 1 Then ApplyLevel()
        End SyncLock
        Return New Releaser()
    End Function

    ''' <summary>手动退出（配合 Quiet 使用，一般不直接调）。</summary>
    Private Sub Release()
        SyncLock GetType(ModLogGate)
            If GateDepth <= 0 Then Return
            GateDepth -= 1
            If GateDepth = 0 Then ApplyLevel()
        End SyncLock
    End Sub

    ''' <summary>按当前层数与调试模式设置日志级别。</summary>
    Private Sub ApplyLevel()
        Try
            If Logger.Instance Is Nothing Then Return
            '批量任务中强制 Info，避免逐文件 Trace 拖慢磁盘
            '不在批量任务中时，跟随调试模式
            Logger.Instance.MinLevel = If(GateDepth > 0, LogLevel.Info, If(ModeDebug, LogLevel.Trace, LogLevel.Info))
        Catch ex As Exception
            '日志级别调整失败不该影响业务流程
        End Try
    End Sub

    Private Class Releaser
        Implements IDisposable

        Private Done As Boolean = False

        Public Sub Dispose() Implements IDisposable.Dispose
            If Done Then Return
            Done = True
            Release()
        End Sub
    End Class

End Module
