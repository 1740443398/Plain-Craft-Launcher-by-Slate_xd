Public Class PageInstanceOverall

    Private IsLoad As Boolean = False
    Private Sub PageSetupLaunch_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

        '重复加载部分
        PanBack.ScrollToHome()

        '更新设置
        Reload()

        '非重复加载部分
        If IsLoad Then Return
        IsLoad = True
        PanDisplay.TriggerForceResize()

    End Sub

    Public InstanceHeaderItem As MyListItem
    ''' <summary>
    ''' 确保当前页面上的信息已正确显示。
    ''' </summary>
    Private Sub Reload()
        AniControlEnabled += 1

        '刷新设置项目
        ComboDisplayType.SelectedIndex = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "DisplayType", McInstanceCardType.Auto)
        BtnDisplayStar.Text = If(PageInstanceLeft.Instance.IsStar, "从收藏夹中移除", "加入收藏夹")
        BtnFolderMods.Visibility = If(PageInstanceLeft.Instance.Modable, Visibility.Visible, Visibility.Collapsed)
        '刷新版本显示
        PanDisplayItem.Children.Clear()
        InstanceHeaderItem = PageInstanceLeft.Instance.ToListItem().Init()
        InstanceHeaderItem.IsHitTestVisible = False
        PanDisplayItem.Children.Add(InstanceHeaderItem)
        FrmMain.PageNameRefresh()
        '刷新版本图标
        ComboDisplayLogo.SelectedIndex = 0
        Dim Logo As String = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "Logo", "")
        Dim LogoCustom As Boolean = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "LogoCustom", "False")
        If LogoCustom Then
            For Each Selection As MyComboBoxItem In ComboDisplayLogo.Items
                If Selection.Tag = Logo OrElse (Selection.Tag = "PCL\Logo.png" AndAlso Logo.EndsWithF("PCL\Logo.png")) Then
                    ComboDisplayLogo.SelectedItem = Selection
                    Exit For
                End If
            Next
        End If

        AniControlEnabled -= 1
    End Sub

#Region "卡片：个性化"

    '版本分类
    Private Sub ComboDisplayType_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboDisplayType.SelectionChanged
        If Not (IsLoad AndAlso AniControlEnabled = 0) Then Return
        If ComboDisplayType.SelectedIndex <> 1 Then
            '改为不隐藏
            Try
                '若设置分类为可安装 Mod，则显示正常的 Mod 管理页面
                WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "DisplayType", ComboDisplayType.SelectedIndex)
                PageInstanceLeft.Instance.DisplayType = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "DisplayType", McInstanceCardType.Auto)
                FrmInstanceLeft.RefreshModDisabled()

                WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "") '要求刷新缓存
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            Catch ex As Exception
                Logger.Error(ex, $"修改版本分类失败（{PageInstanceLeft.Instance.Name}）")
            End Try
            Reload() '更新 “打开 Mod 文件夹” 按钮
        Else
            '改为隐藏
            Try
                If Not Settings.Get(Of Boolean)("HintHide") Then
                    If MyMsgBox("确认要从版本列表中隐藏该版本吗？隐藏该版本后，它将不再出现于 PCL 显示的版本列表中。" & vbCrLf & "此后，在版本列表页面按下 F11 才可以查看被隐藏的版本。", "隐藏版本提示",, "取消") <> 1 Then
                        ComboDisplayType.SelectedIndex = 0
                        Return
                    End If
                    Settings.Set("HintHide", True)
                End If
                WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "DisplayType", McInstanceCardType.Hidden)
                WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "") '要求刷新缓存
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            Catch ex As Exception
                Logger.Error(ex, $"隐藏版本 {PageInstanceLeft.Instance.Name} 失败")
            End Try
        End If
    End Sub

    '更改描述
    Private Sub BtnDisplayDesc_Click(sender As Object, e As EventArgs) Handles BtnDisplayDesc.Click
        Try
            Dim OldInfo As String = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "CustomInfo")
            Dim NewInfo As String = MyMsgBoxInput("更改描述", "修改版本的描述文本，留空则使用 PCL 的默认描述。", OldInfo, New ObjectModel.Collection(Of Validate), "默认描述")
            If NewInfo IsNot Nothing AndAlso OldInfo <> NewInfo Then WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "CustomInfo", NewInfo)
            PageInstanceLeft.Instance = New McInstance(PageInstanceLeft.Instance.Name).Load()
            Reload()
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, $"版本 {PageInstanceLeft.Instance.Name} 描述更改失败", LogBehavior.Alert)
        End Try
    End Sub

    '重命名版本
    Private Sub BtnDisplayRename_Click(sender As Object, e As EventArgs) Handles BtnDisplayRename.Click
        Try
            '确认输入的新名称
            Dim OldName As String = PageInstanceLeft.Instance.Name
            Dim OldPath As String = PageInstanceLeft.Instance.PathVersion
            '修改此部分的同时修改快速安装的版本名检测*
            Dim NewName As String = MyMsgBoxInput("重命名版本", "", OldName, New ObjectModel.Collection(Of Validate) From {
                New ValidateFolderName(McFolderSelected & "versions", IgnoreList:=New List(Of String) From {OldName})
            })
            If String.IsNullOrWhiteSpace(NewName) Then Return
            Dim NewPath As String = McFolderSelected & "versions\" & NewName & "\"
            '重新读取版本 JSON 信息，避免 JsonObject 中已被合并的项被重新存储
            Dim JsonObject As JObject
            Dim OldJsonPath As String = PageInstanceLeft.Instance.GetJsonPath()
            Try
                JsonObject = FileUtils.ReadAsJson(OldJsonPath)
            Catch ex As Exception
                Logger.Warn(ex, "在重命名读取 JSON 时失败")
                JsonObject = PageInstanceLeft.Instance.JsonObject
            End Try
            '重命名主文件夹
            DirectoryUtils.Move(OldPath, NewPath)
            '清理 ini 缓存
            PageInstanceLeft.Instance.ResetSettingsCache()
            '重命名 jar 文件与 natives 文件夹
            '不能进行遍历重命名，否则在版本名很短的时候容易误伤其他文件（#6443）
            If DirectoryUtils.Exists($"{NewPath}{OldName}-natives") Then DirectoryUtils.Move($"{NewPath}{OldName}-natives", $"{NewPath}{NewName}-natives")
            If FileUtils.Exists($"{NewPath}{OldName}.jar") Then FileUtils.Move($"{NewPath}{OldName}.jar", $"{NewPath}{NewName}.jar")
            '替换版本设置文件中的路径
            If FileUtils.Exists(NewPath & "PCL\Setup.ini") Then
                FileUtils.Write(NewPath & "PCL\Setup.ini", FileUtils.ReadAsString(NewPath & "PCL\Setup.ini").Replace(OldPath, NewPath))
            End If
            '更改已选中的版本
            If ReadIni(McFolderSelected & "PCL.ini", "Version") = OldName Then
                WriteIni(McFolderSelected & "PCL.ini", "Version", NewName)
            End If
            '更新版本 Json
            Try
                JsonObject("id") = NewName
                FileUtils.Delete(NewPath & PathUtils.GetLastPart(OldJsonPath))
                FileUtils.Write(NewPath & NewName & ".json", JsonObject.ToString)
            Catch ex As Exception
                Logger.Warn(ex, "重命名版本 json 失败")
            End Try
            '刷新与提示
            Hint("重命名成功！", HintType.Green)
            PageInstanceLeft.Instance = New McInstance(NewName).Load()
            If Not IsNothing(McInstanceSelected) AndAlso McInstanceSelected.Equals(PageInstanceLeft.Instance) Then WriteIni(McFolderSelected & "PCL.ini", "Version", NewName)
            Reload()
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, "重命名版本失败", LogBehavior.Alert)
        End Try
    End Sub

    '版本图标
    Private Sub ComboDisplayLogo_SelectionChanged() Handles ItemDisplayLogoCustom.PreviewMouseLeftButtonUp, ComboDisplayLogo.SelectionChanged
        If Not (IsLoad AndAlso AniControlEnabled = 0) Then Return
        '选择 自定义 时修改图片
        Try
            If ComboDisplayLogo.SelectedItem Is ItemDisplayLogoCustom Then
                Dim FileName As String = Dialogs.SelectFile("选择图片", False, filter:={({"png", "jpeg", "jpg", "gif", "webp"}, "常用图片文件")}).FirstOrDefault()
                If String.IsNullOrEmpty(FileName) Then
                    Reload() '还原选项
                    Return
                End If
                FileUtils.Copy(FileName, PageInstanceLeft.Instance.PathVersion & "PCL\Logo.png")
            Else
                FileUtils.Delete(PageInstanceLeft.Instance.PathVersion & "PCL\Logo.png")
            End If
        Catch ex As Exception
            Logger.Error(ex, $"更改自定义版本图标失败（{PageInstanceLeft.Instance.Name}）")
        End Try
        '进行更改
        Try
            Dim NewLogo As String = ComboDisplayLogo.SelectedItem.Tag
            WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "Logo", NewLogo)
            WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "LogoCustom", Not NewLogo = "")
            '刷新显示
            WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "") '要求刷新缓存
            PageInstanceLeft.Instance = New McInstance(PageInstanceLeft.Instance.Name).Load()
            Reload()
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, $"更改版本图标失败（{PageInstanceLeft.Instance.Name}）")
        End Try
    End Sub

    '收藏夹
    Private Sub BtnDisplayStar_Click(sender As Object, e As EventArgs) Handles BtnDisplayStar.Click
        Try
            WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL\Setup.ini", "IsStar", Not PageInstanceLeft.Instance.IsStar)
            PageInstanceLeft.Instance = New McInstance(PageInstanceLeft.Instance.Name).Load()
            Reload()
            McInstanceListForceRefresh = True
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, $"版本 {PageInstanceLeft.Instance.Name} 收藏状态更改失败", LogBehavior.Alert)
        End Try
    End Sub

#End Region

#Region "卡片：快捷方式"

    '版本文件夹
    Private Sub BtnFolderVersion_Click() Handles BtnFolderVersion.Click
        OpenInstanceFolder(PageInstanceLeft.Instance)
    End Sub
    Public Shared Sub OpenInstanceFolder(Instance As McInstance)
        OpenExplorer(Instance.PathVersion)
    End Sub

    '存档文件夹
    Private Sub BtnFolderSaves_Click() Handles BtnFolderSaves.Click
        Dim Folder As String = PageInstanceLeft.Instance.PathIndie & "saves\"
        DirectoryUtils.Create(Folder)
        OpenExplorer(Folder)
    End Sub

    'Mod 文件夹
    Private Sub BtnFolderMods_Click() Handles BtnFolderMods.Click
        Dim Folder As String = PageInstanceLeft.Instance.PathIndie & "mods\"
        DirectoryUtils.Create(Folder)
        OpenExplorer(Folder)
    End Sub

    '截图文件夹
    Private Sub BtnFolderScreen_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnFolderScreen.Click
        Dim Folder As String = PageInstanceLeft.Instance.PathIndie & "screenshots\"
        DirectoryUtils.Create(Folder)
        OpenExplorer(Folder)
    End Sub

#End Region

#Region "卡片：管理"

    '导出启动脚本
    Private Sub BtnManageScript_Click() Handles BtnManageScript.Click
        Try
            '弹窗要求指定脚本的保存位置
            Dim SavePath As String = Dialogs.SaveFile("选择脚本保存位置", "启动 " & PageInstanceLeft.Instance.Name & ".bat", filter:={("bat", "批处理文件")})
            If SavePath Is Nothing Then Return
            '检查中断（等玩家选完弹窗指不定任务就结束了呢……）
            If McLaunchLoader.State = LoadState.Loading Then
                Hint("请在当前启动任务结束后再试！", HintType.Red)
                Return
            End If
            '生成脚本
            If McLaunchStart(New McLaunchOptions With {.SaveBatch = SavePath, .Instance = PageInstanceLeft.Instance}) Then
                If Settings.Get(Of McLoginType)("LoginType") = McLoginType.Legacy Then
                    Hint("正在导出启动脚本……")
                Else
                    Hint("正在导出启动脚本……（注意，使用脚本启动可能会导致登录失效！）")
                End If
            End If
        Catch ex As Exception
            Logger.Error(ex, $"导出启动脚本失败（{PageInstanceLeft.Instance.Name}）", LogBehavior.Alert)
        End Try
    End Sub

    '补全文件
    Private Sub BtnManageCheck_Click(sender As Object, e As EventArgs) Handles BtnManageCheck.Click
        Try
            '忽略文件检查提示
            If ShouldIgnoreFileCheck(PageInstanceLeft.Instance) Then
                Hint("请先关闭 [版本设置 → 设置 → 高级启动选项 → 关闭文件校验]，然后再尝试补全文件！", HintType.Blue)
                Return
            End If
            '重复任务检查
            For Each OngoingLoader In LoaderTaskbar
                If OngoingLoader.Name <> PageInstanceLeft.Instance.Name & " 文件补全" Then Continue For
                Hint("正在处理中，请稍候！", HintType.Red)
                Return
            Next
            '启动
            Dim Loader As New LoaderCombo(Of String)(PageInstanceLeft.Instance.Name & " 文件补全", DlClientFix(PageInstanceLeft.Instance, True, False))
            Loader.OnStateChanged =
            Sub()
                Select Case Loader.State
                    Case LoadState.Finished
                        Hint(Loader.Name & "成功！", HintType.Green)
                    Case LoadState.Failed
                        Hint(Loader.Name & "失败：" & Loader.Error.GetDisplay(False), HintType.Red)
                    Case LoadState.Canceled
                        Hint(Loader.Name & "已取消！", HintType.Blue)
                End Select
            End Sub
            Loader.Start(PageInstanceLeft.Instance.Name)
            LoaderTaskbarAdd(Loader)
            FrmMain.BtnExtraDownload.ShowRefresh()
            FrmMain.BtnExtraDownload.Ribble()
        Catch ex As Exception
            Logger.Error(ex, $"尝试补全文件失败（{PageInstanceLeft.Instance.Name}）", LogBehavior.Alert)
        End Try
    End Sub

    '删除版本
    '修改此代码时，同时修改 PageSelectRight 中的代码
    Private Sub BtnManageDelete_Click(sender As Object, e As EventArgs) Handles BtnManageDelete.Click
        Try
            Dim IsShiftPressed As Boolean = My.Computer.Keyboard.ShiftKeyDown
            Dim IsHintIndie As Boolean = PageInstanceLeft.Instance.State <> McInstanceState.Error AndAlso PageInstanceLeft.Instance.PathIndie <> McFolderSelected
            Dim SavesFolder = DirectoryUtils.GetInfo(PageInstanceLeft.Instance.PathIndie & "saves\")
            Dim SaveEntries = If(IsHintIndie AndAlso SavesFolder.Exists, SavesFolder.EnumerateDirectories.OrderByDescending(Function(Save) Save.LastWriteTime).Select(Function(Save) $"{Save.Name}（上次修改：{StringUtils.FormatTimeSpan(Save.LastWriteTime - Date.Now, False)}）").ToList, New List(Of String))
            Select Case MyMsgBox($"你确定要{If(IsShiftPressed, "永久", "")}删除版本 {PageInstanceLeft.Instance.Name} 吗？" &
                        If(IsHintIndie, vbCrLf & "该版本对应的存档、资源包、Mod 等文件也将被一并删除！" &
                            If(SaveEntries.Any, vbCrLf & vbCrLf & "这会删除以下存档：" & vbCrLf & "· " & SaveEntries.Join(vbCrLf & "· "), ""), ""),
                        "版本删除确认", , "取消",, IsHintIndie OrElse IsShiftPressed)
                Case 1
                    PageInstanceLeft.Instance.ResetSettingsCache()
                    DirectoryUtils.Delete(PageInstanceLeft.Instance.PathVersion, Not IsShiftPressed)
                    Hint("已删除版本 " & PageInstanceLeft.Instance.Name & "！", HintType.Green)
                Case 2
                    Return
            End Select
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            FrmMain.PageBack()
        Catch ex As OperationCanceledException
            Logger.Warn(ex, $"删除版本 {PageInstanceLeft.Instance.Name} 被主动取消")
        Catch ex As Exception
            Logger.Error(ex, $"删除版本 {PageInstanceLeft.Instance.Name} 失败", LogBehavior.Alert)
        End Try
    End Sub

#End Region

#Region "版本迁移与 Mod 更新"

    ''' <summary>
    ''' 版本迁移：把当前实例的 Mod 换成目标 MC 版本对应的构建，
    ''' 同时迁移 config / resourcepacks / kubejs / defaultconfigs。
    '''
    ''' 流程：选目标版本 → 确认 loader → 后台扫描 → 预览报告 → 执行。
    ''' 没有对应版本的 Mod 会明确列出，不静默丢弃。
    ''' </summary>
    Private Sub BtnManageMigrate_Click(sender As Object, e As EventArgs) Handles BtnManageMigrate.Click
        Try
            Dim Inst = PageInstanceLeft.Instance
            If Inst Is Nothing Then Return

            Dim CurVer As String = ""
            Try
                CurVer = Inst.Version?.VanillaName
            Catch
            End Try

            Dim ModsDir = IO.Path.Combine(If(Inst.PathIndie, Inst.PathVersion), "mods")
            If Not IO.Directory.Exists(ModsDir) Then
                Hint("这个版本没有 mods 文件夹，没有需要迁移的 Mod。", HintType.Blue)
                Return
            End If

            '步骤 1：选目标版本
            Hint("正在获取可选版本列表……", HintType.Blue)
            Dim Versions As List(Of String) = Nothing
            Dim VerErr As Exception = Nothing
            RunInNewThread(
                Sub()
                    Try
                        Versions = ModMigration.FetchTargetVersions(False, 40)
                    Catch ex As Exception
                        VerErr = ex
                    End Try
                End Sub, "FetchMigrateVersions").Join()
            If VerErr IsNot Nothing Then Throw VerErr
            If Versions Is Nothing OrElse Versions.Count = 0 Then
                Hint("获取版本列表失败，请检查网络后重试。", HintType.Red)
                Return
            End If

            Versions.RemoveAll(Function(V) String.Equals(V, CurVer, StringComparison.OrdinalIgnoreCase))
            If Versions.Count = 0 Then
                Hint("没有可迁移的目标版本。", HintType.Blue)
                Return
            End If

            Dim Radios As New List(Of IMyRadio)
            For Each V In Versions
                Radios.Add(New MyRadioBox With {.Text = V})
            Next
            Dim PickVer = MyMsgBoxSelect(Radios,
                $"选择要迁移到的游戏版本（当前：{If(CurVer = "", "未知", CurVer)}）", "下一步", "取消")
            If PickVer Is Nothing Then Return
            Dim TargetVer = Versions(PickVer.Value)

            '步骤 2：确定 loader
            Dim Loader = ModMigration.GuessLoader(Inst)
            If Loader = "" Then
                Dim LoaderRadios As New List(Of IMyRadio) From {
                    New MyRadioBox With {.Text = "Fabric"},
                    New MyRadioBox With {.Text = "Forge"},
                    New MyRadioBox With {.Text = "NeoForge"}
                }
                Dim PickLoader = MyMsgBoxSelect(LoaderRadios,
                    $"无法判断当前 Mod 加载器，请选择（目标：{TargetVer}）：", "下一步", "取消")
                If PickLoader Is Nothing Then Return
                Loader = {"fabric", "forge", "neoforge"}(PickLoader.Value)
            End If

            '步骤 3：后台扫描
            Hint($"正在扫描 Mod（{TargetVer} / {Loader}）……", HintType.Blue)
            Dim Items As List(Of ModMigration.MigrationItem) = Nothing
            Dim ScanErr As Exception = Nothing
            RunInNewThread(
                Sub()
                    Try
                        Items = ModMigration.ScanForMigration(ModsDir, TargetVer, Loader)
                    Catch ex As Exception
                        ScanErr = ex
                    End Try
                End Sub, "ScanMigrateMods").Join()
            If ScanErr IsNot Nothing Then Throw ScanErr
            If Items Is Nothing OrElse Items.Count = 0 Then
                Hint("没有扫描到可解析的 Mod。", HintType.Blue)
                Return
            End If

            '步骤 4：预览报告
            Dim CanMig = Items.Where(Function(I) I.CanMigrate).ToList()
            Dim Cannot = Items.Where(Function(I) Not I.CanMigrate).ToList()

            Dim Report As New Text.StringBuilder
            Report.AppendLine($"当前：{If(CurVer = "", "未知", CurVer)}  →  目标：{TargetVer}（{Loader}）")
            Report.AppendLine($"共 {Items.Count} 个 Mod，可迁移 {CanMig.Count} 个，无对应版本 {Cannot.Count} 个。")
            Report.AppendLine()
            If CanMig.Count > 0 Then
                Report.AppendLine("【将更新】")
                For Each I In CanMig.Take(20)
                    Report.AppendLine($"  · {I.ModName}：{I.CurrentVersion} → {I.TargetVersion}")
                Next
                If CanMig.Count > 20 Then Report.AppendLine($"  …… 还有 {CanMig.Count - 20} 个")
                Report.AppendLine()
            End If
            If Cannot.Count > 0 Then
                Report.AppendLine("【目标版本无构建，保留原文件不动】")
                For Each I In Cannot.Take(20)
                    Report.AppendLine($"  · {I.ModName}（{I.FileName}）")
                Next
                If Cannot.Count > 20 Then Report.AppendLine($"  …… 还有 {Cannot.Count - 20} 个")
            End If
            Report.AppendLine()
            Report.AppendLine("配置 / 资源包 / kubejs / defaultconfigs 会一并复制（不覆盖已有文件）。")

            If MyMsgBox(Report.ToString, "版本迁移预览", "开始迁移", "取消") <> 1 Then Return

            '步骤 5：执行
            Dim OkCount = 0
            Dim FailList As New List(Of String)
            RunInNewThread(
                Sub()
                    For Each I In CanMig
                        Try
                            If ModMigration.DownloadMigrated(I, ModsDir) Then
                                If I.FileName <> I.TargetFileName Then ModMigration.RemoveOldMod(ModsDir, I.FileName)
                                SyncLock FailList
                                    OkCount += 1
                                End SyncLock
                            Else
                                SyncLock FailList
                                    FailList.Add(I.ModName)
                                End SyncLock
                            End If
                        Catch ex As Exception
                            Logger.Warn(ex, $"迁移 {I.ModName} 失败")
                            SyncLock FailList
                                FailList.Add(I.ModName)
                            End SyncLock
                        End Try
                    Next
                End Sub, "MigrateMods").Join()

            '配置与资源包
            Try
                ModMigration.MigrateConfigsAndPacks(Inst.PathIndie, Inst.PathIndie, TargetVer)
            Catch ex As Exception
                Logger.Warn(ex, "迁移配置与资源包失败")
            End Try

            Dim Msg = $"迁移完成：成功 {OkCount} 个。"
            If FailList.Count > 0 Then
                Msg &= vbCrLf & $"失败 {FailList.Count} 个：" & vbCrLf & "· " & FailList.Take(10).Join(vbCrLf & "· ")
            End If
            If Cannot.Count > 0 Then
                Msg &= vbCrLf & vbCrLf & $"另有 {Cannot.Count} 个 Mod 在 {TargetVer} 上没有可用构建，已保留原文件。"
            End If
            Hint(Msg, If(FailList.Count > 0, HintType.Blue, HintType.Green))

        Catch ex As OperationCanceledException
            Logger.Warn(ex, "版本迁移被取消")
        Catch ex As Exception
            Logger.Error(ex, "版本迁移失败", LogBehavior.Alert)
        End Try
    End Sub

    ''' <summary>
    ''' 检查当前实例已安装 Mod 是否有更新（同 MC 版本 + 同 loader）。
    ''' 只检查并提示，用户确认后才下载。
    ''' </summary>
    Private Sub BtnManageModVersion_Click(sender As Object, e As EventArgs) Handles BtnManageModVersion.Click
        Try
            Dim Inst = PageInstanceLeft.Instance
            If Inst Is Nothing Then Return
            Dim CurVer As String = ""
            Try
                CurVer = Inst.Version?.VanillaName
            Catch
            End Try
            If CurVer = "" Then
                Hint("无法确定当前游戏版本。", HintType.Red)
                Return
            End If
            Dim Loader = ModMigration.GuessLoader(Inst)
            If Loader = "" Then
                Hint("这个版本没有安装 Mod 加载器（Fabric/Forge/NeoForge），无需检查。", HintType.Blue)
                Return
            End If

            Dim ModsDir = IO.Path.Combine(If(Inst.PathIndie, Inst.PathVersion), "mods")
            If Not IO.Directory.Exists(ModsDir) Then
                Hint("这个版本没有 mods 文件夹。", HintType.Blue)
                Return
            End If

            Hint("正在检查 Mod 更新……", HintType.Blue)
            Dim Items As List(Of ModMigration.MigrationItem) = Nothing
            Dim Err As Exception = Nothing
            RunInNewThread(
                Sub()
                    Try
                        Items = ModMigration.ScanForMigration(ModsDir, CurVer, Loader)
                    Catch ex As Exception
                        Err = ex
                    End Try
                End Sub, "CheckModUpdates").Join()
            If Err IsNot Nothing Then Throw Err
            If Items Is Nothing OrElse Items.Count = 0 Then
                Hint("没有扫描到可解析的 Mod。", HintType.Blue)
                Return
            End If

            Dim Updates = Items.Where(Function(I) I.CanMigrate AndAlso
                Not String.IsNullOrWhiteSpace(I.TargetVersion) AndAlso
                Not String.Equals(I.TargetVersion, I.CurrentVersion, StringComparison.OrdinalIgnoreCase)).ToList()
            If Updates.Count = 0 Then
                Hint($"已是最新（{Items.Count} 个 Mod 均无更新）。", HintType.Green)
                Return
            End If

            Dim Report As New Text.StringBuilder
            Report.AppendLine($"{CurVer} / {Loader} 下共 {Items.Count} 个 Mod，{Updates.Count} 个有新版本：")
            Report.AppendLine()
            For Each I In Updates.Take(25)
                Report.AppendLine($"  · {I.ModName}：{I.CurrentVersion} → {I.TargetVersion}")
            Next
            If Updates.Count > 25 Then Report.AppendLine($"  …… 还有 {Updates.Count - 25} 个")
            Report.AppendLine()
            Report.AppendLine("更新会下载新版本并删除旧文件。")

            If MyMsgBox(Report.ToString, "Mod 更新检查", "全部更新", "稍后") <> 1 Then Return

            Dim OkCount = 0
            Dim FailList As New List(Of String)
            RunInNewThread(
                Sub()
                    For Each I In Updates
                        Try
                            If ModMigration.DownloadMigrated(I, ModsDir) Then
                                If I.FileName <> I.TargetFileName Then ModMigration.RemoveOldMod(ModsDir, I.FileName)
                                SyncLock FailList
                                    OkCount += 1
                                End SyncLock
                            Else
                                SyncLock FailList
                                    FailList.Add(I.ModName)
                                End SyncLock
                            End If
                        Catch ex As Exception
                            Logger.Warn(ex, $"更新 {I.ModName} 失败")
                            SyncLock FailList
                                FailList.Add(I.ModName)
                            End SyncLock
                        End Try
                    Next
                End Sub, "UpdateMods").Join()

            Dim Msg = $"更新完成：成功 {OkCount} 个。"
            If FailList.Count > 0 Then Msg &= vbCrLf & $"失败 {FailList.Count} 个：" & vbCrLf & "· " & FailList.Take(10).Join(vbCrLf & "· ")
            Hint(Msg, If(FailList.Count > 0, HintType.Blue, HintType.Green))

        Catch ex As OperationCanceledException
            Logger.Warn(ex, "检查 Mod 更新被取消")
        Catch ex As Exception
            Logger.Error(ex, "检查 Mod 更新失败", LogBehavior.Alert)
        End Try
    End Sub

#End Region

End Class
