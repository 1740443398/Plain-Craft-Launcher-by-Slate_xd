Imports System.IO
Imports System.Diagnostics
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading

''' <summary>
''' 一键开服器页面：按用途选核心 → 选版本 → 下载 → 启动。
''' </summary>
Public Class PageServerMain
    Inherits MyPageRight
    Implements IRefreshable

    ''' <summary>内置预设模板。</summary>
    Private Class ServerTemplate
        Public Property Name As String = ""
        Public Property Description As String = ""
        Public Property MemoryMB As Integer = 2048
        Public Property OnlineMode As Boolean = True
    End Class

    Private ReadOnly Templates As New List(Of ServerTemplate) From {
        New ServerTemplate With {.Name = "和朋友玩", .Description = "4 GB 内存，开启正版验证，适合熟人小服。", .MemoryMB = 4096, .OnlineMode = True},
        New ServerTemplate With {.Name = "轻量测试", .Description = "1 GB 内存，关闭正版验证，适合快速验证模组或命令。", .MemoryMB = 1024, .OnlineMode = False},
        New ServerTemplate With {.Name = "大型整合包服", .Description = "8 GB 内存，开启正版验证，给重模组整合包用。", .MemoryMB = 8192, .OnlineMode = True},
        New ServerTemplate With {.Name = "离线局域网", .Description = "2 GB 内存，关闭正版验证，只在家里/宿舍局域网用。", .MemoryMB = 2048, .OnlineMode = False}
    }

    '当前选中的核心与类别
    Private SelectedCore As ModServerCore.CoreOption = Nothing
    '核心列表是否展开全部（默认只看推荐）
    Private _ShowAllCores As Boolean = False
    Private SelectedKind As ModServerCore.CoreKind = ModServerCore.CoreKind.Plugin
    Private CoresLoaded As Boolean = False

    '当前下载目标目录名
    Private DownloadTarget As String = ""
    
    '设置下拉框选中项时会触发 SelectionChanged，导致同一个构建列表被请求两次
    Private SuppressBuildReload As Boolean = False
    '每次请求构建时递增。回调回来时比对，不一致说明用户已经换过选择，丢弃旧结果
    Private BuildRequestGeneration As Integer = 0

    '==== 开服向导 ====
    '向导就是 4 屏：0=选类型 1=选核心 2=选版本 3=配置。
    '步骤条直接用 WizardStep 同步，不做「按数据反推」——那种推算会让步骤条
    '和真实屏幕错位（下载完还在第 3 屏，条却点满了）。
    Private WizardStep As Integer = 0
    Private WizardInited As Boolean = False

    '配置面板只为「当前那个服务端版本」构建；版本变了就得重建
    Private ConfigPanelBuilt As Boolean = False
    Private ConfigVersion As ModServer.ServerVersion = Nothing
    Private ConfigFields As New Dictionary(Of String, Func(Of Object))

    ''' <summary>Java 前置检查：控制向导顶部的提示卡显隐与文案。</summary>
    Private Sub CheckJava()
        Try
            If PanJavaHint Is Nothing Then Return
            '判断标准：启动器管理的 Java 目录或 PATH 上有能跑的 java
            Dim J = ModServer.FindJava()
            Dim HasJava = False
            If Not String.IsNullOrWhiteSpace(J) Then
                HasJava = True
            Else
                '找启动器自己装的：exe 旁的 PCL\Java\*in\java.exe
                Try
                    Dim Base = IO.Path.Combine(Paths.Base, "PCL", "Java")
                    If IO.Directory.Exists(Base) Then
                        For Each D In IO.Directory.GetDirectories(Base)
                            If IO.File.Exists(IO.Path.Combine(D, "bin", "java.exe")) Then HasJava = True
                        Next
                    End If
                Catch
                End Try
            End If
            If HasJava Then
                PanJavaHint.Visibility = Visibility.Collapsed
            Else
                PanJavaHint.Visibility = Visibility.Visible
                LabJavaHint.Text = "没找到可用的 Java。开 1.17+ 的服需要 Java 17 或更高。点下面的按钮一键安装（约 40 MB），或到「设置 → Java」手动配置。"
            End If
        Catch ex As Exception
            Logger.Warn(ex, "Java 检查失败")
        End Try
    End Sub

    '==== 托管外部服务器 ====
    'Windows 不让事后接管别人进程的 stdin/stdout，所以托管 = 自动搜索 java 进程
    '+ RCON 发命令 + latest.log 增量跟踪。没开 RCON 就只给日志并说明怎么开。
    Private AttachProc As ModAttach.AttachServer = Nothing
    Private AttachRcon As ModAttach.RconClient = Nothing
    Private AttachTail As ModAttach.LogTail = Nothing
    Private AttachTimer As Threading.DispatcherTimer = Nothing

    '下载进度节流：63MB 的 jar 会触发几千次回调，
    '每次都启动 120ms 动画会把 UI 线程压垮（11-08 事故的根因），改成 100ms 节流 + 直接设宽
    Private LastProgressTick As Long = 0

    Private Sub PageLoaded() Handles Me.Loaded
        '把服务端输出接到控制台
        ModServer.OutputHandler = Sub(Line As String) RunInUi(Sub() AppendConsole(Line))
        Refresh()
        RenderTemplates()
        '订阅左侧栏：不订阅的话点了没反应（事件发了没人听）
        If FrmServerLeft IsNot Nothing Then
            RemoveHandler FrmServerLeft.PageChanged, AddressOf OnSidebarChanged
            AddHandler FrmServerLeft.PageChanged, AddressOf OnSidebarChanged
        End If
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Try
            ModServer.ScanLocal()
            RenderLocalVersions()
        Catch ex As Exception
            Logger.Error(ex, "刷新服务端列表失败")
            Hint($"刷新失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    '==== 本地服务端 ====

    Private Sub RenderLocalVersions()
        Try
            PanServerList.Children.Clear()
            Dim All = ModServer.LocalVersions

            If All.Count = 0 Then
                LabLocalSummary.Text = "还没有安装任何服务端。"
                PanServerEmpty.Visibility = Visibility.Visible
                RenderGuideSteps()
                Return
            End If
            PanServerEmpty.Visibility = Visibility.Collapsed

            Dim ReadyCount = All.Where(Function(ver) ver.Installed AndAlso ver.EulaAccepted).Count()
            LabLocalSummary.Text = $"共 {All.Count} 个服务端，{ReadyCount} 个可启动" &
                                   If(ModServer.IsRunning, $"，当前运行：{ModServer.RunningVersion?.Name}", "。")

            For Each V In All
                PanServerList.Children.Add(BuildVersionCard(V))
            Next
        Catch ex As Exception
            Logger.Error(ex, "渲染服务端列表失败")
        End Try
    End Sub

    ''' <summary>新手引导：三步走，每步都能直接点。</summary>
    Private Sub RenderGuideSteps()
        Try
            If PanServerEmptyInner Is Nothing Then Return
            '每次渲染前清空：Refresh() 会反复进入这里，不清会叠加 N 遍
            PanServerEmptyInner.Children.Clear()

            '这里只在它下面追加三步引导，谁后到谁清空就会把对方抹掉

            PanServerEmptyInner.Children.Add(New TextBlock With {
                .Text = "三步开服", .FontSize = 15, .FontWeight = FontWeights.SemiBold,
                .HorizontalAlignment = HorizontalAlignment.Center, .Margin = New Thickness(0, 0, 0, 16)
            })

            Dim StepNums = {"1", "2", "3"}
            Dim StepTitles = {"下载服务端核心", "同意 EULA", "启动并进游戏"}
            Dim StepDescs = {
                "到左侧「下载核心」，先按用途选类型，再选核心和版本下载。",
                "首次启动会提示同意 Minecraft EULA，这是 Mojang 的要求。",
                "点「启动」，然后在游戏里用「直连」连 127.0.0.1:端口。"
            }
            For k = 0 To StepNums.Length - 1
                Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 14)}
                Dim Num As New Border With {
                    .Width = 26, .Height = 26, .CornerRadius = New CornerRadius(13),
                    .Background = New SolidColorBrush(Color.FromRgb(&H4A, &H7C, &HD0)),
                    .Margin = New Thickness(0, 0, 12, 0),
                    .Child = New TextBlock With {
                        .Text = StepNums(k), .Foreground = Brushes.White, .FontWeight = FontWeights.Bold,
                        .HorizontalAlignment = HorizontalAlignment.Center,
                        .VerticalAlignment = VerticalAlignment.Center
                    }
                }
                Row.Children.Add(Num)
                Dim Txt As New StackPanel
                Txt.Children.Add(New TextBlock With {.Text = StepTitles(k), .FontWeight = FontWeights.SemiBold})
                Txt.Children.Add(New TextBlock With {
                    .Text = StepDescs(k), .TextWrapping = TextWrapping.Wrap, .FontSize = 12,
                    .Margin = New Thickness(0, 3, 0, 0),
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
                })
                Row.Children.Add(Txt)
                PanServerEmptyInner.Children.Add(Row)
            Next

            Dim Go As New MyButton With {
                .Text = "去下载服务端", .Height = 34, .MinWidth = 150,
                .HorizontalAlignment = HorizontalAlignment.Center, .Margin = New Thickness(0, 6, 0, 0)
            }
            AddHandler Go.Click, Sub()
                                     Try
                                         If FrmServerLeft IsNot Nothing Then FrmServerLeft.SetPage(1)
                                         ShowSection(1)
                                     Catch ex As Exception
                                         Logger.Warn(ex, "跳转到下载页失败")
                                     End Try
                                 End Sub
            PanServerEmptyInner.Children.Add(Go)
        Catch ex As Exception
            Logger.Error(ex, "渲染引导失败")
        End Try
    End Sub

    ''' <summary>一个服务端版本的卡片。</summary>
    Private Function BuildVersionCard(V As ModServer.ServerVersion) As FrameworkElement
        Dim IsThis = ModServer.RunningVersion Is V AndAlso ModServer.IsRunning
        Dim Card As New MyCard With {
            .Title = V.Name & If(IsThis, "（运行中）", ""),
            .Margin = New Thickness(0, 0, 0, 10)
        }
        Dim Panel As New StackPanel With {.Margin = New Thickness(25, 36, 25, 14)}

        Dim StatusColor = If(V.Installed AndAlso V.EulaAccepted,
                             Color.FromRgb(&H2E, &H8B, &H74), Color.FromRgb(&HC0, &H88, &H20))
        Panel.Children.Add(New TextBlock With {
            .Text = V.ReadyText, .FontSize = 12.5,
            .Foreground = New SolidColorBrush(StatusColor)
        })

        Dim Info As String = $"端口 {V.Port} · 内存 {V.MemoryMB} MB · " &
                             If(V.OnlineMode, "正版验证", "离线模式") &
                             If(V.Installed, $" · {StringUtils.FormatByteSize(V.JarSize)}", "")
        Panel.Children.Add(New TextBlock With {
            .Text = Info, .Margin = New Thickness(0, 5, 0, 0), .FontSize = 12,
            .TextWrapping = TextWrapping.Wrap,
            .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
        })

        Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 12, 0, 0)}

        If V.Installed Then
            Dim BtnStart As New MyButton With {
                .Text = If(IsThis, "停止", "启动"), .Height = 32, .MinWidth = 90
            }
            AddHandler BtnStart.Click, Sub()
                                           If ModServer.IsRunning AndAlso ModServer.RunningVersion Is V Then
                                               ModServer.StopServer()
                                           Else
                                               If Not V.EulaAccepted Then
                                                   If MyMsgBox("开服前必须同意 Minecraft EULA（https://aka.ms/MinecraftEULA）。" & vbCrLf & vbCrLf &
                                                               "同意后会在该版本目录写入 eula.txt。要同意并继续吗？",
                                                               "Minecraft EULA", "同意", "取消") <> 1 Then Return
                                                   ModServer.AcceptEula(V)
                                               End If
                                               ModServer.StartServer(V)
                                           End If
                                           Refresh()
                                       End Sub
            Row.Children.Add(BtnStart)
        End If

        Dim BtnSettings As New MyButton With {.Text = "配置", .Height = 32, .MinWidth = 80, .Margin = New Thickness(10, 0, 0, 0)}
        AddHandler BtnSettings.Click, Sub() EditVersionConfig(V)
        Row.Children.Add(BtnSettings)

        Dim BtnFolder As New MyButton With {.Text = "打开目录", .Height = 32, .MinWidth = 100, .Margin = New Thickness(10, 0, 0, 0)}
        AddHandler BtnFolder.Click, Sub()
                                        Try
                                            DirectoryUtils.Create(V.Dir)
                                            Process.Start("explorer.exe", $"""{V.Dir}""")
                                        Catch ex As Exception
                                            Logger.Warn(ex, "打开服务端目录失败")
                                        End Try
                                    End Sub
        Row.Children.Add(BtnFolder)

        Dim BtnDelete As New MyButton With {.Text = "删除", .Height = 32, .MinWidth = 80, .Margin = New Thickness(10, 0, 0, 0)}
        AddHandler BtnDelete.Click, Sub()
                               '正在运行的服务端不能删：目录被占用，世界存档也可能写一半
                               If ModServer.IsRunning AndAlso ModServer.RunningVersion Is V Then
                                   Hint("这个服务端正在运行，请先停止再删除", HintType.Red)
                                   Return
                               End If
                               
                               If MyMsgBox($"确定删除服务端「{V.Name}」吗？" & vbCrLf & vbCrLf &
                                           "整个目录（含世界存档）都会被删除，无法撤销。",
                                           "删除服务端", "删除", "取消", IsWarn:=True) <> 1 Then Return
                               ModServer.DeleteVersion(V)
                               Refresh()
        End Sub
        Row.Children.Add(BtnDelete)

        Panel.Children.Add(Row)
        Card.Children.Add(Panel)
        Return Card
    End Function

    ''' <summary>修改某个服务端的端口/内存/正版验证。</summary>
    Private Sub EditVersionConfig(V As ModServer.ServerVersion)
        Try
            Dim PortStr As String = MyMsgBoxInput("端口", "服务端监听端口，默认 25565。",
                DefaultInput:=V.Port.ToString(),
                ValidateRules:=New System.Collections.ObjectModel.Collection(Of Validate) From {New ValidateInteger(1024, 65535)})
            If PortStr Is Nothing Then Return

            Dim MemStr As String = MyMsgBoxInput("内存", "分配给服务端的内存（MB）。建议不低于 1024。",
                DefaultInput:=V.MemoryMB.ToString(),
                ValidateRules:=New System.Collections.ObjectModel.Collection(Of Validate) From {New ValidateInteger(512, 65536)})
            If MemStr Is Nothing Then Return

            Dim OnlineChoice As Integer = MyMsgBox(
                "开启正版验证吗？" & vbCrLf & vbCrLf &
                "开启：只有正版账号能进（online-mode=true）。" & vbCrLf &
                "关闭：离线账号也能进，但无法防冒名（online-mode=false）。",
                "正版验证", If(V.OnlineMode, "保持开启", "关闭"), If(V.OnlineMode, "关闭", "保持开启"))
            If OnlineChoice = 0 Then Return

            V.Port = CInt(Val(PortStr))
            V.MemoryMB = CInt(Val(MemStr))
            If OnlineChoice = 1 Then V.OnlineMode = V.OnlineMode Else V.OnlineMode = Not V.OnlineMode
            ModServer.SaveConfig(V)
            Refresh()
            Hint("配置已保存", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "修改服务端配置失败")
        End Try
    End Sub

    '==== 下载核心 ====

    ''' <summary>加载核心列表并渲染类别筛选。</summary>
    Private Sub LoadCores()
        '数据在「选择实例服务端」步骤用时再拉（PrepareCoreStep），这里只做占位
        If ModServerCore.Cores.Count = 0 Then
            RunInNewThread(Sub() ModServerCore.FetchCores(), "WizardPreFetch")
        End If
    End Sub

    ''' <summary>渲染当前类别下的核心。</summary>
    Private Sub RenderCoreList()
        Try
            PanCoreList.Children.Clear()
            Dim List = ModServerCore.GetByKind(SelectedKind)
            If List.Count = 0 Then
                PanCoreList.Children.Add(New TextBlock With {
                    .Text = "这个类别暂时没有可用核心。", .TextWrapping = TextWrapping.Wrap,
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
                })
                Return
            End If

            For Each C In List
                Dim Card As New MyCard With {
                    .Title = C.Name & If(C.Recommend, "  ★ 推荐", ""),
                    .Margin = New Thickness(0, 0, 0, 8)
                }
                Dim P As New StackPanel With {.Margin = New Thickness(20, 32, 20, 12)}
                P.Children.Add(New TextBlock With {
                    .Text = C.Description, .TextWrapping = TextWrapping.Wrap, .FontSize = 12.5
                })
                P.Children.Add(New TextBlock With {
                    .Text = $"{C.KindText} · {C.AbilityText} · 支持 {C.Versions.Count} 个版本",
                    .TextWrapping = TextWrapping.Wrap, .FontSize = 12, .Margin = New Thickness(0, 5, 0, 0),
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
                })
                Dim Btn As New MyButton With {
                    .Text = "选这个核心", .Height = 30, .MinWidth = 120,
                    .Margin = New Thickness(0, 10, 0, 0), .HorizontalAlignment = HorizontalAlignment.Left
                }
                AddHandler Btn.Click, Sub() SelectCore(C)
                P.Children.Add(Btn)
                Card.Children.Add(P)
                PanCoreList.Children.Add(Card)
            Next
        Catch ex As Exception
            Logger.Error(ex, "渲染核心列表失败")
        End Try
    End Sub

    ''' <summary>选中一个核心，填充版本下拉框。</summary>
    Private Sub SelectCore(C As ModServerCore.CoreOption)
        Try
            SelectedCore = C
            SelectedVersion = ""
            SelectedBuild = Nothing
            RefreshWizardNav()
        Catch ex As Exception
            Logger.Error(ex, "选中核心失败")
        End Try
    End Sub

    '==== 模板 ====

    Private Sub RenderTemplates()
        Try
            PanTemplateList.Children.Clear()
            For Each T In Templates
                Dim Card As New MyCard With {.Title = T.Name, .Margin = New Thickness(0, 0, 0, 8)}
                Dim P As New StackPanel With {.Margin = New Thickness(25, 34, 25, 12)}
                P.Children.Add(New TextBlock With {
                    .Text = T.Description, .TextWrapping = TextWrapping.Wrap, .FontSize = 12.5,
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
                })
                Dim Btn As New MyButton With {
                    .Text = "用此模板创建", .Height = 30, .MinWidth = 130, .Margin = New Thickness(0, 10, 0, 0),
                    .HorizontalAlignment = HorizontalAlignment.Left
                }
                AddHandler Btn.Click, Sub() CreateFromTemplate(T)
                P.Children.Add(Btn)
                Card.Children.Add(P)
                PanTemplateList.Children.Add(Card)
            Next
        Catch ex As Exception
            Logger.Error(ex, "渲染模板失败")
        End Try
    End Sub

    Private Sub CreateFromTemplate(T As ServerTemplate)
        Try
            Dim Name As String = MyMsgBoxInput("服务端版本名",
                "填一个名字作为目录名，建议直接用游戏版本号，方便对应。",
                HintText:="例如：1.20.1")
            If String.IsNullOrWhiteSpace(Name) Then Return
            Name = Name.Trim()
            For Each C In Path.GetInvalidFileNameChars()
                Name = Name.Replace(C, "_"c)
            Next
            If ModServer.LocalVersions.Any(Function(ver) ver.Name = Name) Then
                Hint("同名服务端已存在", HintType.Red)
                Return
            End If

            Dim V As New ModServer.ServerVersion With {
                .Name = Name, .MemoryMB = T.MemoryMB, .OnlineMode = T.OnlineMode,
                .Motd = $"{T.Name} - {Name}"
            }
            ModServer.SaveConfig(V)
            ModServer.ScanLocal()
            Refresh()
            Hint($"已按「{T.Name}」创建 {Name}，接下来请下载对应核心", HintType.Green)
            If FrmServerLeft IsNot Nothing Then FrmServerLeft.SetPage(1)
            ShowSection(1)
        Catch ex As Exception
            Logger.Error(ex, "从模板创建失败")
        End Try
    End Sub

    '==== 控制台 ====

    Public Sub AppendConsole(Line As String)
        Try
            If TextConsoleOutput Is Nothing Then Return
            Dim Stamp = Date.Now.ToString("HH:mm:ss")
            TextConsoleOutput.Text &= $"[{Stamp}] {Line}" & vbCrLf
            If TextConsoleOutput.Text.Length > 200000 Then
                TextConsoleOutput.Text = TextConsoleOutput.Text.Substring(TextConsoleOutput.Text.Length - 100000)
            End If
            ScrollConsole.ScrollToEnd()
        Catch
        End Try
    End Sub

    Private Sub RenderConsole()
        Try
            If AttachRcon IsNot Nothing AndAlso AttachProc IsNot Nothing Then
                LabConsoleStatus.Text = $"已附加外部服务端：PID {AttachProc.PID}（RCON 端口 {AttachProc.RconPort}）。命令走 RCON，输出来自日志。"
                BtnConsoleSend.IsEnabled = True
                BtnConsoleStop.IsEnabled = False '进程不是我们起的，别替玩家做停止的决定
            ElseIf ModServer.IsRunning Then
                LabConsoleStatus.Text = $"正在运行：{ModServer.RunningVersion?.Name}（端口 {ModServer.RunningVersion?.Port}）"
                BtnConsoleSend.IsEnabled = True
                BtnConsoleStop.IsEnabled = True
            Else
                LabConsoleStatus.Text = "当前没有运行中的服务器。可到「服务端」页启动，或在下方「托管外部服务器」附加一个已在运行的。"
                BtnConsoleSend.IsEnabled = False
                BtnConsoleStop.IsEnabled = False
            End If
        Catch ex As Exception
            Logger.Error(ex, "刷新控制台状态失败")
        End Try
    End Sub

    Private Sub BtnConsoleSend_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnConsoleSend.MouseLeftButtonUp
        Try
            Dim Cmd As String = If(TextConsoleCommand.Text, "").Trim()
            If Cmd = "" Then Return
            '已附加外部服务端时，命令走 RCON，不走本进程
            If AttachRcon IsNot Nothing AndAlso AttachProc IsNot Nothing Then
                Try
                    Dim Reply = AttachRcon.Exec(Cmd)
                    AppendConsole("[RCON] " & Cmd)
                    If Not String.IsNullOrEmpty(Reply) Then
                        For Each L In Reply.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                            AppendConsole(L)
                        Next
                    End If
                    TextConsoleCommand.Text = ""
                Catch ex As Exception
                    Hint("RCON 命令发送失败：" & ex.Message, HintType.Red)
                    DetachExternal()
                End Try
                Return
            End If
            If Not ModServer.IsRunning Then
                Hint("服务器没有在运行", HintType.Red)
                Return
            End If
            ModServer.SendCommand(Cmd)
            AppendConsole("> " & Cmd)
            TextConsoleCommand.Text = ""
        Catch ex As Exception
            Logger.Error(ex, "发送命令失败")
        End Try
    End Sub

    Private Sub BtnConsoleStop_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnConsoleStop.MouseLeftButtonUp
        If Not ModServer.IsRunning Then Return
        If MyMsgBox("确定停止服务器吗？" & vbCrLf & vbCrLf &
                    "会先发送 stop 命令让服务器保存世界，15 秒未退出才会强制结束。",
                    "停止服务器", "停止", "取消", IsWarn:=True) <> 1 Then Return
        ModServer.StopServer()
        RenderConsole()
    End Sub

    Private Sub BtnConsoleClear_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnConsoleClear.MouseLeftButtonUp
        Try
            TextConsoleOutput.Text = ""
        Catch
        End Try
    End Sub

    '==== 状态 ====

    Private Sub RenderStatus()
        Try
            PanStatusDetail.Children.Clear()
            If Not ModServer.IsRunning Then
                LabStatusSummary.Text = "当前没有运行中的服务器。"
                Return
            End If

            Dim V = ModServer.RunningVersion
            Dim Proc = ModServer.Running
            Dim Uptime As TimeSpan = TimeSpan.Zero
            Dim MemMB As Double = 0
            Try
                If Proc IsNot Nothing AndAlso Not Proc.HasExited Then
                    Uptime = Date.Now - Proc.StartTime
                    MemMB = Proc.WorkingSet64 / 1048576.0
                End If
            Catch
            End Try

            LabStatusSummary.Text = $"正在运行：{V?.Name}"

            Dim Card As New MyCard With {.Title = "实时数据", .Margin = New Thickness(0, 0, 0, 12)}
            Dim G As New Grid With {.Margin = New Thickness(25, 36, 25, 16)}
            For k = 0 To 3
                G.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(30)})
            Next
            G.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(130)})
            G.ColumnDefinitions.Add(New ColumnDefinition)

            Dim RowLabels = {"服务器", "端口", "已运行", "内存占用"}
            Dim RowValues = {
                If(V?.Name, "-"),
                If(V?.Port.ToString(), "-"),
                If(Uptime.TotalHours >= 1, $"{CInt(Uptime.TotalHours)} 小时 {Uptime.Minutes} 分", $"{Uptime.Minutes} 分 {Uptime.Seconds} 秒"),
                $"{Math.Round(MemMB, 1)} MB"
            }
            For k = 0 To 3
                Dim Lbl As New TextBlock With {
                    .Text = RowLabels(k), .VerticalAlignment = VerticalAlignment.Center,
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H99, &H99, &H99))
                }
                Grid.SetRow(Lbl, k) : Grid.SetColumn(Lbl, 0) : G.Children.Add(Lbl)

                Dim Val As New TextBlock With {.Text = RowValues(k), .VerticalAlignment = VerticalAlignment.Center}
                Grid.SetRow(Val, k) : Grid.SetColumn(Val, 1) : G.Children.Add(Val)
            Next
            Card.Children.Add(G)
            PanStatusDetail.Children.Add(Card)
        Catch ex As Exception
            Logger.Error(ex, "渲染状态失败")
        End Try
    End Sub

    Private Sub BtnStatusRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStatusRefresh.MouseLeftButtonUp
        RenderStatus()
    End Sub

    '==== 其它 ====

    Private Sub BtnServerRescan_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnServerRescan.MouseLeftButtonUp
        Refresh()
        Hint("已重新扫描", HintType.Green)
    End Sub

    Private Sub BtnServerFolder_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnServerFolder.MouseLeftButtonUp
        Try
            DirectoryUtils.Create(ModServer.ServerDir)
            Process.Start("explorer.exe", $"""{ModServer.ServerDir}""")
        Catch ex As Exception
            Logger.Warn(ex, "打开目录失败")
        End Try
    End Sub

    ''' <summary>切换到指定子页面（0=服务端 1=下载核心 2=控制台 3=状态 4=模板）。</summary>
    Public Sub ShowSection(ID As Integer)
        Try
            PanSecVersions.Visibility = If(ID = 0, Visibility.Visible, Visibility.Collapsed)
            PanSecDownload.Visibility = If(ID = 1, Visibility.Visible, Visibility.Collapsed)
            PanSecConsole.Visibility = If(ID = 2, Visibility.Visible, Visibility.Collapsed)
            PanSecStatus.Visibility = If(ID = 3, Visibility.Visible, Visibility.Collapsed)
            PanSecTemplates.Visibility = If(ID = 4, Visibility.Visible, Visibility.Collapsed)
            '步骤条只在「下载核心」子页有向导语义，其余子页藏掉
            PanStepCard.Visibility = If(ID = 1, Visibility.Visible, Visibility.Collapsed)
            If ID = 0 Then Refresh()
            If ID = 1 Then
                LoadCores()
                    EnsureWizardStep()
            End If
            If ID = 2 Then RenderConsole()
            If ID = 3 Then RenderStatus()
            If ID = 4 Then RenderTemplates()
            '切子页做一次短淡入：命名动画，可被快速连点安全替换
            AnimateSectionIn(GetSectionPanel(ID))
        Catch ex As Exception
            Logger.Error(ex, $"切换服务端子页失败：{ID}")
        End Try
    End Sub

    Private Function GetSectionPanel(ID As Integer) As FrameworkElement
        Select Case ID
            Case 0 : Return PanSecVersions
            Case 1 : Return PanSecDownload
            Case 2 : Return PanSecConsole
            Case 3 : Return PanSecStatus
            Case 4 : Return PanSecTemplates
            Case Else : Return PanSecVersions
        End Select
    End Function

    ''' <summary>
    ''' 子页淡入：180ms ease-out，名字固定 ServerSectionFade，
    ''' AniStart 会自动替换上一组，快速连点不会叠出多层动画。
    ''' 先把 Opacity 归零、再用固定增量 1。AaOpacity 的参数是「改变的值」：
    ''' 若不归零而写 1 - Target.Opacity，重复触发时会算出零增量，动画失效——
    ''' 这正是测试 srv-no-zero-delta 禁掉的写法。
    ''' </summary>
    Private Sub AnimateSectionIn(Target)
        Try
            If Target Is Nothing Then Return
            Target.Opacity = 0
            AniStart(AaOpacity(Target, 1, 180, 0, New AniEaseOutFluent(AniEasePower.Weak)), "ServerSectionFade")
        Catch ex As Exception
            Logger.Warn(ex, "子页淡入失败")
        End Try
    End Sub


    '==== 下载进度与下一步 ====

    ''' <summary>
    ''' 显示下载进度。Ratio 为 -1 表示总量未知（不显示百分比，只显示已下载量）。
    ''' </summary>
    Private Sub ShowDownloadProgress(Ratio As Double, Label As String)
        Try
            '63MB 的 jar 会触发几千次回调，每次都重排 UI 会把线程压垮：
            '100ms 节流，中间的回调直接丢弃（最后 100% 由完成卡片兜底）
            Dim Now As Long = CLng(DateTime.Now.TimeOfDay.TotalMilliseconds)
            If Now - LastProgressTick < 100 AndAlso Ratio > 0 AndAlso Ratio < 1 Then Return
            LastProgressTick = Now

            PanDownloadProgress.Visibility = Visibility.Visible
            Dim Pct As Integer = CInt(Math.Max(0, Math.Min(100, Ratio * 100)))
            LabProgressText.Text = If(Ratio < 0,
                $"{Label}：正在下载……",
                $"{Label}：{Pct}%")

            '按可用宽度算条宽，不写死像素；直接赋值，不每帧启动动画
            Dim Full As Double = 0
            Try
                Full = Math.Max(0, PanDownloadProgress.ActualWidth)
            Catch
            End Try
            If Full <= 0 Then Full = 400
            Dim TargetWidth As Double = Math.Max(0, Full * Math.Max(0, Math.Min(1, Ratio)))
            BarDownload.Width = TargetWidth
        Catch ex As Exception
            '失败也要把条摆到目标位置：宽度的直接赋值不依赖动画系统
            Dim TargetWidth As Double = 0
            BarDownload.Width = TargetWidth
            Logger.Warn(ex, "更新下载进度失败")
        End Try
    End Sub

    Private Sub HideDownloadProgress()
        Try
            PanDownloadProgress.Visibility = Visibility.Collapsed
            BarDownload.Width = 0
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 
    Private Sub BtnJavaRetry_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaRetry.MouseLeftButtonUp
        CheckJava()
        If PanJavaHint.Visibility = Visibility.Collapsed Then
            Hint("已检测到 Java", HintType.Green)
        Else
            Hint("仍然没有找到 Java", HintType.Red)
        End If
    End Sub

    Private Sub BtnJavaGoSetup_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaGoSetup.MouseLeftButtonUp
        Try
            FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch)
        Catch ex As Exception
            Logger.Warn(ex, "跳转到 Java 设置失败")
        End Try
    End Sub

    ''' <summary>左侧栏切换时同步右侧内容。</summary>
    Private Sub OnSidebarChanged(sender As Object, id As Integer)
        ShowSection(id)
    End Sub

    '==== 开服向导（简幻欢式）：5 步 = 类别 → 服务端 → 版本 → 规格 → 确认创建 ====
    '左竖向时间线 + 右内容卡 + 右下角 返回/继续；下载集中在最后一步「创建」。

    Private WizardCat As String = "JE"
    Private SelectedBuild As ModServerCore.CoreFile = Nothing
    Private SpecMemory As Integer = 2048
    Private SelectedVersion As String = ""
    Private SelectedBuildInfo As String = ""

    Private Sub EnsureWizardStep()
        Try
            If WizardInited Then
                RefreshWizardNav()
                Return
            End If
            WizardInited = True
            WizardStep = 0
            For i = 0 To 4
                Dim Card = WizardCard(i)
                If Card IsNot Nothing Then
                    Card.Visibility = If(i = 0, Visibility.Visible, Visibility.Collapsed)
                    Card.Opacity = 1
                    SetWizardCardX(Card, 0)
                End If
            Next
            EnsureWizardCat()
            RefreshWizardNav()
        Catch ex As Exception
            Logger.Warn(ex, "初始化开服向导失败")
        End Try
    End Sub

    Private Function WizardGateError() As String
        Select Case WizardStep
            Case 0 : Return If(WizardCat = "", "请先选择类别", "")
            Case 1 : Return If(SelectedCore Is Nothing, "请先选择服务端", "")
            Case 2 : Return If(SelectedBuild Is Nothing, "请先选择版本", "")
            Case 3 : Return If(SpecMemory <= 0, "请先选择规格", "")
            Case Else : Return ""
        End Select
    End Function

    Private Function CanWizardGoNext() As Boolean
        Return WizardGateError() = ""
    End Function

    Private Sub RefreshWizardNav()
        Try
            If BtnWizardNext Is Nothing Then Return
            RefreshStepRail()
            Dim AtEnd As Boolean = WizardStep >= 4
            BtnWizardPrev.Visibility = If(WizardStep <= 0, Visibility.Collapsed, Visibility.Visible)
            Dim Gate = WizardGateError()
            If AtEnd Then
                BtnWizardNext.Text = "创建并启动"
                BtnWizardNext.IsEnabled = True
            Else
                BtnWizardNext.Text = If(Gate = "", "继续", Gate)
                BtnWizardNext.IsEnabled = (Gate = "")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "刷新向导导航失败")
        End Try
    End Sub

    Private Sub RefreshStepRail()
        Try
            If PanStepRail Is Nothing Then Return
            PanStepRail.Children.Clear()
            Dim Names = {"选择实例类别", "选择实例服务端", "选择实例版本", "选择实例规格", "确认信息并创建"}
            For i = 0 To Names.Length - 1
                If i > 0 Then
                    PanStepRail.Children.Add(New Shapes.Rectangle With {
                        .Height = 16, .Width = 1.5, .HorizontalAlignment = HorizontalAlignment.Left,
                        .Margin = New Thickness(11, 2, 0, 2),
                        .Fill = New SolidColorBrush(If(i <= WizardStep,
                            ResColor("ColorBrush3"),
                            ResColor("ColorBrushGray5")))})
                End If

                Dim Dot As New Border With {
                    .Width = 24, .Height = 24, .CornerRadius = New CornerRadius(12),
                    .HorizontalAlignment = HorizontalAlignment.Left}
                Dim Icon As New TextBlock With {
                    .FontFamily = New FontFamily("Segoe MDL2 Assets"), .FontSize = 11,
                    .HorizontalAlignment = HorizontalAlignment.Center, .VerticalAlignment = VerticalAlignment.Center}
                If i < WizardStep Then
                    Dot.Background = New SolidColorBrush(ResColor("ColorBrush3"))
                    Icon.Text = ChrW(&HE73E)
                    Icon.Foreground = Brushes.White
                ElseIf i = WizardStep Then
                    Dot.Background = Brushes.White
                    Dot.BorderBrush = New SolidColorBrush(ResColor("ColorBrush3"))
                    Dot.BorderThickness = New Thickness(1.5)
                    Icon.Text = ChrW(&HE70F)
                    Icon.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3")
                Else
                    Dot.BorderBrush = New SolidColorBrush(ResColor("ColorBrushGray5"))
                    Dot.BorderThickness = New Thickness(1.5)
                End If
                Dot.Child = Icon

                Dim Lab As New TextBlock With {
                    .Text = Names(i), .FontSize = 12, .Margin = New Thickness(8, 0, 0, 0),
                    .VerticalAlignment = VerticalAlignment.Center}
                If i <= WizardStep Then
                    Lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3")
                    If i = WizardStep Then Lab.FontWeight = FontWeights.SemiBold
                Else
                    Lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                End If

                Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Height = 24}
                Row.Children.Add(Dot)
                Row.Children.Add(Lab)
                PanStepRail.Children.Add(Row)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "渲染向导时间线失败")
        End Try
    End Sub

    Private Sub GoWizardStep(Target As Integer)
        Try
            Dim N As Integer = Math.Max(0, Math.Min(4, Target))
            If N = WizardStep Then Return
            If N > WizardStep Then
                Dim Gate = WizardGateError()
                If Gate <> "" Then
                    Hint(Gate, HintType.Red)
                    Return
                End If
            End If
            If N = 1 Then PrepareCoreStep()
            If N = 2 Then PrepareVersionStep()
            If N = 3 Then PrepareSpecStep()
            If N = 4 Then PrepareSummaryStep()

            Dim Old As Integer = WizardStep
            WizardStep = N
            AnimateWizardStep(N, Old)
            RefreshWizardNav()
            ScrollWizardTop()
        Catch ex As Exception
            Logger.Warn(ex, "切换向导步骤失败")
        End Try
    End Sub

    Private Sub ScrollWizardTop()
        Try
            Dim Sc = TryCast(FindName("Content"), MyScrollViewer)
            If Sc IsNot Nothing AndAlso PanStepCard IsNot Nothing Then
                Dim Pos As Point = PanStepCard.TransformToAncestor(Sc).Transform(New Point(0, 0))
                Dim Target As Double = Math.Max(0, Sc.VerticalOffset + Pos.Y - 12)
                If Math.Abs(Target - Sc.VerticalOffset) >= 8 Then Sc.ScrollToVerticalOffset(Target)
            End If
        Catch ex As Exception
            '滚动失败不影响流程
        End Try
    End Sub

    Private Sub AnimateWizardStep(NewStep As Integer, OldStep As Integer)
        Try
            Dim Enter = WizardCard(NewStep)
            Dim Leave = WizardCard(OldStep)
            If Enter Is Nothing Then Return
            Dim Dir As Double = If(NewStep >= OldStep, 16, -16)

            If Enter.Visibility <> Visibility.Visible Then
                Enter.Opacity = 0
                SetWizardCardX(Enter, Dir)
                Enter.Visibility = Visibility.Visible
            End If

            Dim Lst As New List(Of ModAnimation.AniData)
            Lst.Add(ModAnimation.AaOpacity(Enter, 1 - Enter.Opacity, 220, 0,
                New ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)))
            Dim EnterX As Double = WizardCardX(Enter)
            If Math.Abs(EnterX) > 0.1 Then
                Lst.Add(ModAnimation.AaX(Enter, 0 - EnterX, 220, 0,
                    New ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)))
            End If

            If Leave IsNot Nothing AndAlso Leave IsNot Enter AndAlso Leave.Visibility = Visibility.Visible Then
                Lst.Add(ModAnimation.AaOpacity(Leave, 0 - Leave.Opacity, 180, 0, New ModAnimation.AniEaseInFluent))
                Dim LeaveX As Double = WizardCardX(Leave)
                Lst.Add(ModAnimation.AaX(Leave, -Dir - LeaveX, 180, 0, New ModAnimation.AniEaseInFluent))
                Dim OldCard = Leave
                Lst.Add(ModAnimation.AaCode(
                    Sub()
                        OldCard.Visibility = Visibility.Collapsed
                        OldCard.Opacity = 1
                        SetWizardCardX(OldCard, 0)
                    End Sub, 180, True))
            End If

            If Lst.Count > 0 Then
                ModAnimation.AniStart(Lst, "WizardStep")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "向导切换动画失败")
            For i = 0 To 4
                Dim Card = WizardCard(i)
                If Card IsNot Nothing Then
                    Card.Visibility = If(i = NewStep, Visibility.Visible, Visibility.Collapsed)
                    Card.Opacity = 1
                    SetWizardCardX(Card, 0)
                End If
            Next
        End Try
    End Sub

    Private Function WizardCard(Index As Integer) As FrameworkElement
        Select Case Index
            Case 0 : Return CardStep1
            Case 1 : Return CardStep2
            Case 2 : Return CardStep3
            Case 3 : Return CardStep4
            Case 4 : Return CardStep5
            Case Else : Return Nothing
        End Select
    End Function

    Private Function WizardCardX(El As FrameworkElement) As Double
        Dim T = TryCast(El.RenderTransform, TranslateTransform)
        If T Is Nothing Then Return 0
        Return T.X
    End Function

    Private Sub SetWizardCardX(El As FrameworkElement, X As Double)
        Dim T = TryCast(El.RenderTransform, TranslateTransform)
        If T Is Nothing Then
            T = New TranslateTransform()
            El.RenderTransform = T
        End If
        T.X = X
    End Sub

    ''' <summary>构造一个可选中的 tile 卡片（简幻欢式选项格）。</summary>
    Private Function ResColor(Key As String) As Color
        Return CType(FindResource(Key), SolidColorBrush).Color
    End Function

    Private Function MakeTile(Title As String, Desc As String, Selected As Boolean, OnClick As Action) As Border
        Dim Tile As New Border With {
            .BorderBrush = New SolidColorBrush(ResColor("ColorBrushGray5")),
            .BorderThickness = New Thickness(1), .CornerRadius = New CornerRadius(5),
            .Background = Brushes.Transparent, .Padding = New Thickness(14, 10, 14, 10),
            .Margin = New Thickness(0, 0, 12, 12), .MinWidth = 190, .Cursor = Cursors.Hand}
        Dim St As New StackPanel
        St.Children.Add(New TextBlock With {.Text = Title, .FontWeight = FontWeights.SemiBold, .FontSize = 13})
        If Desc <> "" Then
            St.Children.Add(New TextBlock With {.Text = Desc, .FontSize = 11.5,
                .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 3, 0, 0),
                .Foreground = New SolidColorBrush(ResColor("ColorBrushGray3"))})
        End If
        Tile.Child = St
        If Selected Then
            Tile.BorderBrush = New SolidColorBrush(ResColor("ColorBrush3"))
            Tile.Background = New SolidColorBrush(ResColor("ColorBrush7"))
        End If
        AddHandler Tile.MouseLeftButtonUp, Sub(a As Object, b As MouseButtonEventArgs) OnClick()
        Return Tile
    End Function

    ''' <summary>第 1 步：类别 tile。JE / BE 可选，其余注明未支持。</summary>
    Private Sub EnsureWizardCat()
        Try
            If PanCatList.Children.Count > 0 Then Return
            Dim Wrap As New WrapPanel
            Wrap.Children.Add(MakeTile("Minecraft JE", "Java 版：原版 / 插件 / 模组 / 代理端", (WizardCat = "JE"),
                Sub()
                    WizardCat = "JE"
                    SelectedCore = Nothing
                    SelectedVersion = ""
                    SelectedBuild = Nothing
                    RefreshWizardNav()
                    EnsureWizardCat()
                End Sub))

            PanCatList.Children.Add(Wrap)
        Catch ex As Exception
            Logger.Error(ex, "渲染类别 tile 失败")
        End Try
    End Sub

    ''' <summary>第 2 步：按类别列服务端 tile，数据来自 FastMirror。</summary>
    Private Sub PrepareCoreStep()
        Try
            EnsureWizardCat()
            Dim KindWanted = ModServerCore.CoreKind.Plugin
            LabCoreStatus.Text = "正在获取服务端列表……"
            RunInNewThread(
                Sub()
                    Dim Ok = If(ModServerCore.Cores.Count > 0, True, ModServerCore.FetchCores())
                    RunInUi(
                        Sub()
                            If Not Ok Then
                                LabCoreStatus.Text = "获取失败，请检查网络后重试。"
                                Return
                            End If
                            Dim List = ModServerCore.Cores.Where(Function(c) c.Kind <> ModServerCore.CoreKind.Bedrock).ToList()
                            '默认只显示推荐核心 + 已选中的核心，其余的折叠在「显示全部」里。
                            'FastMirror 现在返回 14 个核心，全部平铺会让这一步长得看不到底，
                            '而绝大多数人只需要推荐的那几个。
                            Dim Recommended = List.Where(Function(c) c.Recommend).ToList()
                            Dim ShowAll As Boolean = _ShowAllCores
                            Dim Display As List(Of ModServerCore.CoreOption) =
                                If(ShowAll, List,
                                   Recommended.Concat(List.Where(Function(c) Not c.Recommend AndAlso
                                       SelectedCore IsNot Nothing AndAlso SelectedCore.Name = c.Name)).Distinct().ToList())
                            LabCoreStatus.Text = If(ShowAll,
                                $"Java 版服务端 · 共 {List.Count} 个（已展开全部）",
                                $"Java 版服务端 · 推荐 {Recommended.Count} 个，共 {List.Count} 个")
                            PanCoreList.Children.Clear()
                            Dim Wrap As New WrapPanel
                            For Each C In Display
                                Dim Core = C
                                Dim Desc = If(C.Recommend, "推荐 · ", "") & C.AbilityText
                                Wrap.Children.Add(MakeTile(Core.Name, Desc, SelectedCore IsNot Nothing AndAlso SelectedCore.Name = Core.Name,
                                    Sub()
                                        SelectedCore = Core
                                        SelectedVersion = ""
                                        SelectedBuild = Nothing
                                        RefreshWizardNav()
                                        PrepareCoreStep()
                                    End Sub))
                            Next
                            PanCoreList.Children.Add(Wrap)
                            '展开 / 收起切换
                            If List.Count > Recommended.Count Then
                                PanCoreList.Children.Add(MakeTile(
                                    If(ShowAll, "收起，只看推荐", "显示全部 " & List.Count & " 个核心"),
                                    If(ShowAll, "回到推荐列表", "含 Sponge / BungeeCord / Velocity 等进阶核心"),
                                    False,
                                    Sub()
                                        _ShowAllCores = Not ShowAll
                                        PrepareCoreStep()
                                    End Sub))
                            End If
                        End Sub)
                End Sub, "WizardCores")
        Catch ex As Exception
            Logger.Error(ex, "准备服务端步骤失败")
        End Try
    End Sub

    ''' <summary>第 3 步：版本 tile；选中后后台拉构建并自动选第一个。</summary>
    Private Sub PrepareVersionStep()
        Try
            If SelectedCore Is Nothing Then Return
            PanVersionList.Children.Clear()
            LabVersionStatus.Text = SelectedCore.Name & " 共 " & SelectedCore.Versions.Count & " 个版本"
            Dim Wrap As New WrapPanel
            For Each V In SelectedCore.Versions
                If String.IsNullOrWhiteSpace(V) Then Continue For
                Dim Ver = V
                Wrap.Children.Add(MakeTile(Ver, "", (SelectedVersion = Ver),
                    Sub()
                        SelectedVersion = Ver
                        SelectedBuild = Nothing
                        RefreshWizardNav()
                        PrepareVersionStep()
                        FetchBuildForSelection()
                    End Sub))
            Next
            PanVersionList.Children.Add(Wrap)
            If SelectedVersion <> "" Then FetchBuildForSelection()
        Catch ex As Exception
            Logger.Error(ex, "渲染版本 tile 失败")
        End Try
    End Sub

    Private Sub FetchBuildForSelection()
        Try
            If SelectedCore Is Nothing OrElse SelectedVersion = "" Then Return
            Dim Core = SelectedCore.Name
            Dim Ver = SelectedVersion
            LabBuildStatus.Text = "正在查询 " & Ver & " 的构建……"
            BuildRequestGeneration += 1
            Dim MyGen = BuildRequestGeneration
            RunInNewThread(
                Sub()
                    Dim Builds = ModServerCore.FetchBuilds(Core, Ver)
                    RunInUi(
                        Sub()
                            If MyGen <> BuildRequestGeneration Then Return
                            If Builds.Count = 0 Then
                                LabBuildStatus.Text = Ver & " 暂无可用构建，换个版本试试。"
                                SelectedBuild = Nothing
                                RefreshWizardNav()
                                Return
                            End If
                            SelectedBuild = Builds(0)
                            SelectedBuildInfo = Core & " " & Ver & " · 构建 " & Builds(0).Build
                            LabBuildStatus.Text = "已选构建：" & SelectedBuildInfo
                            RefreshWizardNav()
                        End Sub)
                End Sub, "WizardBuilds")
        Catch ex As Exception
            Logger.Error(ex, "查询构建失败")
        End Try
    End Sub

    ''' <summary>第 4 步：内存规格 tile。</summary>
    Private Sub PrepareSpecStep()
        Try
            PanSpecList.Children.Clear()
            Dim Wrap As New WrapPanel
            '用 (内存, 标题, 说明) 元组数组，避免参差数组在 VB 里退化成 Object() 导致后期绑定崩溃
            Dim Specs As (Mem As Integer, Title As String, Desc As String)() = {
                (512, "512 MB", "1-2 人 · 纯原版小服"),
                (1024, "1 GB", "2-5 人 · 轻量插件"),
                (2048, "2 GB", "5-10 人 · 常规插件"),
                (4096, "4 GB", "10 人+ / 轻模组"),
                (6144, "6 GB", "模组服起步"),
                (8192, "8 GB", "大型整合包")
            }
            For Each Sp In Specs
                Dim Mem As Integer = Sp.Mem
                Wrap.Children.Add(MakeTile(Sp.Title, Sp.Desc, (SpecMemory = Mem),
                    Sub()
                        SpecMemory = Mem
                        RefreshWizardNav()
                        PrepareSpecStep()
                    End Sub))
            Next
            PanSpecList.Children.Add(Wrap)
        Catch ex As Exception
            Logger.Error(ex, "渲染规格 tile 失败")
        End Try
    End Sub

    ''' <summary>第 5 步：确认摘要。</summary>
    Private Sub PrepareSummaryStep()
        Try
            LabSummary.Text = "类别：Minecraft JE" & vbCrLf &
                "服务端：" & If(SelectedCore?.Name, "?") & vbCrLf &
                "版本：" & SelectedVersion & vbCrLf &
                "构建：" & SelectedBuildInfo & vbCrLf &
                "内存：" & SpecMemory & " MB" & vbCrLf & vbCrLf &
                "点「创建并启动」开始下载（约 40 MB，进度显示在下方），同意 EULA 后自动启动并进入控制台。"
            LabCreateStatus.Text = ""
        Catch ex As Exception
            Logger.Error(ex, "渲染确认页失败")
        End Try
    End Sub

    ''' <summary>创建：OneClickStart 一条龙（下载 → EULA → 配置 → 启动 → 控制台）。</summary>
    Private Sub CreateAndStart()
        Try
            BtnWizardNext.IsEnabled = False
            PanDownloadProgress.Visibility = Visibility.Visible
            ShowDownloadProgress(0, SelectedCore.Name & " " & SelectedVersion)

            Dim Build = SelectedBuild
            Dim Mem = SpecMemory
            '预建版本对象把内存带进去：OneClickStart 会沿用传入的 V（含 MemoryMB）
            Dim PreV As New ModServer.ServerVersion With {
                .Name = SelectedVersion & "-" & If(SelectedCore?.Name, "Server"),
                .MemoryMB = Mem}
            Dim V As ModServer.ServerVersion = PreV
            RunInNewThread(
                Sub()
                    Try
                        Dim Err = ModServer.OneClickStart(Build, V,
                            Sub(Stage As String, Ratio As Double)
                                RunInUi(Sub() ShowDownloadProgress(Ratio, Stage))
                            End Sub)
                        RunInUi(
                            Sub()
                                BtnWizardNext.IsEnabled = True
                                If Err = "EULA_NOT_ACCEPTED" Then
                                    HideDownloadProgress()
                                    LabCreateStatus.Text = "核心已下载。需要同意 Minecraft EULA 后启动。"
                                    If MyMsgBox("开服前必须同意 Minecraft EULA。" & vbCrLf & vbCrLf &
                                        "原文见 https://aka.ms/MinecraftEULA" & vbCrLf & vbCrLf &
                                        "同意后会在服务端目录写入 eula.txt。要同意并继续吗？",
                                        "Minecraft EULA", "同意", "取消") = 1 Then
                                        ModServer.AcceptEula(V)
                                        If ModServer.StartServer(V) Then
                                            Hint("服务端已启动", HintType.Green)
                                            Refresh()
                                            If FrmServerLeft IsNot Nothing Then FrmServerLeft.SetPage(2)
                                            ShowSection(2)
                                        Else
                                            LabCreateStatus.Text = "启动失败，详见日志。核心与配置已就绪。"
                                        End If
                                    Else
                                        LabCreateStatus.Text = "已取消。核心已下载，稍后可在「服务端」页启动。"
                                    End If
                                    Refresh()
                                ElseIf Err = "" Then
                                    HideDownloadProgress()
                                    LabCreateStatus.Text = "创建完成，服务端已启动！"
                                    Hint("服务端已启动", HintType.Green)
                                    Refresh()
                                    If FrmServerLeft IsNot Nothing Then FrmServerLeft.SetPage(2)
                                    ShowSection(2)
                                Else
                                    HideDownloadProgress()
                                    LabCreateStatus.Text = Err
                                    Hint(Err, HintType.Red)
                                End If
                            End Sub)
                    Catch ex As Exception
                        Logger.Error(ex, "创建服务端失败")
                        RunInUi(
                            Sub()
                                BtnWizardNext.IsEnabled = True
                                HideDownloadProgress()
                                LabCreateStatus.Text = "创建出错：" & ex.Message
                            End Sub)
                    End Try
                End Sub, "WizardCreate")
        Catch ex As Exception
            BtnWizardNext.IsEnabled = True
            Logger.Error(ex, "触发创建失败")
        End Try
    End Sub

    Private Sub BtnWizardNext_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnWizardNext.Click
        If WizardStep >= 4 Then
            CreateAndStart()
        Else
            GoWizardStep(WizardStep + 1)
        End If
    End Sub

    Private Sub BtnWizardPrev_Click(sender As Object, e As EventArgs) Handles BtnWizardPrev.Click
        GoWizardStep(WizardStep - 1)
    End Sub

    '==== 托管外部服务器：交互 ====

    ''' <summary>自动搜索运行中的 java 服务端进程并填下拉框。</summary>
    Private Sub BtnAttachScan_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAttachScan.MouseLeftButtonUp
        Try
            BtnAttachScan.IsEnabled = False
            LabAttachStatus.Text = "正在扫描运行中的 java 进程……"
            RunInNewThread(
                Sub()
                    Dim Found = ModAttach.ScanExternal()
                    RunInUi(
                        Sub()
                            BtnAttachScan.IsEnabled = True
                            ComboAttach.Items.Clear()
                            If Found.Count = 0 Then
                                LabAttachStatus.Text = "没发现运行中的服务端进程。先在别处启动服务器，或到「服务端」页用 SLATCL 启动。"
                                Return
                            End If
                            For Each A In Found
                                Dim It As New MyComboBoxItem With {.Content = A.Describe}
                                It.Tag = A
                                ComboAttach.Items.Add(It)
                            Next
                            ComboAttach.SelectedIndex = 0
                            LabAttachStatus.Text = $"发现 {Found.Count} 个运行中的服务端。选一个点「附加」。" &
                                If(Found.Any(Function(x) Not x.RconEnabled),
                                   " 注意：其中未开 RCON 的只能看日志，不能发命令。", "")
                        End Sub)
                End Sub, "AttachScan")
        Catch ex As Exception
            BtnAttachScan.IsEnabled = True
            Logger.Error(ex, "触发扫描外部服务端失败")
        End Try
    End Sub

    ''' <summary>附加：RCON 可用就连上并发命令；不可用就只跟日志，并说明原因。</summary>
    Private Sub BtnAttachConnect_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAttachConnect.MouseLeftButtonUp
        Try
            Dim It = TryCast(ComboAttach.SelectedItem, MyComboBoxItem)
            Dim A = TryCast(It?.Tag, ModAttach.AttachServer)
            If A Is Nothing Then
                Hint("先「自动搜索」并选择一个服务端", HintType.Red)
                Return
            End If
            If AttachRcon IsNot Nothing Then DetachExternal()

            AttachProc = A
            AttachTail = New ModAttach.LogTail(IO.Path.Combine(A.Dir, "logs", "latest.log"))

            If A.RconEnabled Then
                If String.IsNullOrWhiteSpace(A.RconPassword) Then
                    LabAttachStatus.Text = "该服务器 enable-rcon=true 但没设 rcon.password，服务端会拒绝 RCON。请补上密码并重启服务器。"
                    Hint("RCON 缺密码，无法附加命令", HintType.Red)
                Else
                    Try
                        AttachRcon = New ModAttach.RconClient()
                        AttachRcon.Connect("127.0.0.1", A.RconPort, A.RconPassword)
                        Hint("已附加：命令将通过 RCON 发送", HintType.Green)
                    Catch ex As Exception
                        AttachRcon = Nothing
                        LabAttachStatus.Text = "RCON 连接失败：" & ex.Message & "。将只显示日志输出。"
                    End Try
                End If
            Else
                LabAttachStatus.Text = "该服务器没开 RCON，所以只能看日志、不能发命令。" & vbCrLf &
                    "想托管命令：在 server.properties 里设 enable-rcon=true 和 rcon.password=你的密码，然后重启该服务器。"
            End If

            '日志尾随：每秒拉一次新增行（输出统一走 AppendConsole，与本服控制台同框显示）
            '重复附加时必须先停掉旧的，否则每附加一次就多一个定时器在跑
            If AttachTimer IsNot Nothing Then
                Try
                    AttachTimer.Stop()
                Catch
                End Try
                AttachTimer = Nothing
            End If
            AttachTimer = New Threading.DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
            AddHandler AttachTimer.Tick,
                Sub(sender2 As Object, e2 As EventArgs)
                    Try
                        If AttachTail Is Nothing Then Return
                        For Each L In AttachTail.Poll()
                            AppendConsole(L)
                        Next
                    Catch ex As Exception
                        '读日志失败不打断定时器
                    End Try
                End Sub
            AttachTimer.Start()

            RenderConsole()
        Catch ex As Exception
            Logger.Error(ex, "附加外部服务端失败")
            Hint("附加失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    ''' <summary>断开附加，释放 RCON 与定时器。</summary>
    Private Sub DetachExternal()
        Try
            If AttachTimer IsNot Nothing Then
                AttachTimer.Stop()
                AttachTimer = Nothing
            End If
            If AttachRcon IsNot Nothing Then
                AttachRcon.Dispose()
                AttachRcon = Nothing
            End If
            AttachProc = Nothing
            AttachTail = Nothing
            Hint("已断开附加", HintType.Blue)
            RenderConsole()
        Catch ex As Exception
            Logger.Warn(ex, "断开附加失败")
        End Try
    End Sub

    Private Sub BtnAttachDetach_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAttachDetach.MouseLeftButtonUp
        DetachExternal()
    End Sub

    '页面卸载时别留下悬着的定时器与 RCON 连接
    Private Sub PageServerMain_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        If AttachTimer IsNot Nothing Then
            AttachTimer.Stop()
        End If
    End Sub

End Class
