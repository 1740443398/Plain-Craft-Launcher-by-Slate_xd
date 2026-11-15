''' <summary>
''' UI 卡死看门狗：检测到界面无响应时自动把诊断信息写进日志。
'''
''' <b>为什么要它</b>：这台机器上「下载时切页面卡死」反复出现，
''' 但纯看日志只能看到「最后一行业务日志」，看不出 UI 线程究竟卡在哪个函数。
''' 有了这个看门狗，下次卡死时日志里会直接写出：
'''   · 卡死了多久
'''   · 进程有多少系统线程、多少句柄
'''   · 各线程的 CPU 占用（找出死循环的线程）
'''   · UI 线程是否真的没响应
'''
''' <b>原理</b>：后台线程每秒检查一次「UI 线程最后一次心跳」。
''' 心跳由 DispatcherTimer（UI 线程）每秒更新。
''' UI 卡住时它自然不跳，这就是检测信号。
'''
''' <b>开销</b>：正常状态下一个 1 秒定时器 + 一次字段写入，可忽略。
''' </summary>
Public Module ModHangWatchdog

    ''' <summary>UI 线程最后一次心跳的时间戳（Environment.TickCount）。</summary>
    Private LastBeat As Integer = 0

    ''' <summary>超过这个秒数没心跳就认为卡死。</summary>
    Private Const HangThresholdSeconds As Integer = 8

    ''' <summary>同一次卡死只 dump 一次，避免日志被刷爆。</summary>
    Private Dumped As Boolean = False

    Private Started As Boolean = False

    ''' <summary>启动看门狗。重复调用安全。</summary>
    Public Sub Start()
        SyncLock GetType(ModHangWatchdog)
            If Started Then Return
            Started = True
            LastBeat = Environment.TickCount
            Dumped = False
        End SyncLock

        'UI 线程心跳：DispatcherTimer 的 Tick 跑在 UI 线程，
        'UI 卡住时它自然就不跳了 —— 这正是我们要检测的信号。
        RunInUi(
            Sub()
                Try
                    Dim BeatTimer As New Threading.DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
                    AddHandler BeatTimer.Tick,
                        Sub()
                            Try
                                Threading.Interlocked.Exchange(LastBeat, Environment.TickCount)
                            Catch
                            End Try
                        End Sub
                    BeatTimer.Start()
                Catch ex As Exception
                    Logger.Warn(ex, "启动卡死检测心跳失败")
                End Try
            End Sub)

        '后台监视线程
        RunInNewThread(
            Sub()
                While True
                    Thread.Sleep(1000)
                    Try
                        Dim Gap As Integer = Environment.TickCount - LastBeat
                        If Gap > HangThresholdSeconds * 1000 Then
                            If Not Dumped Then
                                Dumped = True
                                DumpDiagnostics(Gap)
                            End If
                        ElseIf Gap < 2000 Then
                            Dumped = False '恢复正常，允许下次再抓
                        End If
                    Catch
                    End Try
                End While
            End Sub, "Hang Watchdog")
    End Sub

    ''' <summary>导出卡死时刻的诊断信息。</summary>
    Private Sub DumpDiagnostics(GapMs As Integer)
        Try
            Logger.Error($"=== 检测到界面无响应约 {GapMs \ 1000} 秒，导出诊断信息 ===")
        Catch
        End Try

        Try
            Dim Proc = Diagnostics.Process.GetCurrentProcess()
            'CPU 占用要重新读一次才准
            Proc.Refresh()
            Dim SysThreads = Proc.Threads

            Logger.Error($"系统线程 {SysThreads.Count} 个，句柄 {Proc.HandleCount} 个，内存 {Proc.WorkingSet64 \ 1024 \ 1024} MB")

            '列出 CPU 时间最高的前 10 个线程 —— 死循环的线程会排在最前
            Dim Busy As New List(Of (Id As Integer, Cpu As Double, State As String))
            For Each T As Diagnostics.ProcessThread In SysThreads
                Try
                    Busy.Add((T.Id, T.TotalProcessorTime.TotalMilliseconds, T.ThreadState.ToString()))
                Catch
                    '线程可能已退出，忽略
                End Try
            Next
            Busy.Sort(Function(a, b) b.Cpu.CompareTo(a.Cpu))

            Logger.Error("CPU 占用最高的 10 个线程（累计 CPU 毫秒）：")
            For Each B In Busy.Take(10)
                Logger.Error($"  线程 {B.Id}: {B.Cpu:N0} ms, 状态 {B.State}")
            Next

            '二次确认：向 UI 线程投递一个空任务，看它会不会被执行
            Dim Ui As Threading.Dispatcher = Nothing
            Try
                Ui = Application.Current?.Dispatcher
            Catch
            End Try

            If Ui IsNot Nothing Then
                Dim Finished As Integer = 0
                Try
                    Ui.BeginInvoke(
                        Sub()
                            Threading.Interlocked.Exchange(Finished, 1)
                        End Sub, Threading.DispatcherPriority.Send)
                Catch ex As Exception
                    Logger.Error($"向 UI 线程投递任务失败：{ex.Message}")
                End Try
                Thread.Sleep(3000)
                If Finished = 1 Then
                    Logger.Error("补充确认：UI 线程在 3 秒内恢复了，可能是短暂卡顿而非死锁")
                Else
                    Logger.Error("补充确认：UI 线程 3 秒内未执行任务，确认卡死")
                End If
            Else
                Logger.Error("补充确认：取不到 Application.Current.Dispatcher，跳过")
            End If

            Logger.Error($"=== 诊断信息导出结束（若下次还卡，请把这段日志发出来）===")
        Catch ex As Exception
            Try
                Logger.Error(ex, "导出卡死诊断信息时自身出错")
            Catch
            End Try
        End Try
    End Sub

End Module
