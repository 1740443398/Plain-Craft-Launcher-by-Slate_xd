''' <summary>
''' 投影预览（Litematic Viewer）的下载引导与调用。
''' 小飞机网盘的分享页是 JS 渲染的，启动器无法解析直链，
''' 所以走「打开浏览器下载 → 用户放到指定目录 → 启动器检测调用」的路径。
''' </summary>
Public Module ModTools

    ''' <summary>投影预览分享页 URL。</summary>
    Public Const SchematicViewerShare As String = "https://share.feijipan.com/s/fEzYjRAD"

    ''' <summary>投影预览安装目录（exe 旁的 Tools\SchematicViewer）。</summary>
    Public ReadOnly Property SchematicViewerDir As String
        Get
            Return IO.Path.Combine(Paths.Base, "Tools", "SchematicViewer")
        End Get
    End Property

    ''' <summary>投影预览 exe 的完整路径（已安装则返回，否则 Nothing）。</summary>
    Public ReadOnly Property SchematicViewerExe As String
        Get
            Dim Dir = SchematicViewerDir
            If Not IO.Directory.Exists(Dir) Then Return Nothing
            For Each Name In {"投影预览.exe", "LitematicViewer.exe", "Litematic Viewer.exe"}
                Dim P = IO.Path.Combine(Dir, Name)
                If IO.File.Exists(P) Then Return P
            Next
            For Each D In IO.Directory.GetDirectories(Dir)
                For Each Name In {"投影预览.exe", "LitematicViewer.exe"}
                    Dim P = IO.Path.Combine(D, Name)
                    If IO.File.Exists(P) Then Return P
                Next
            Next
            Return Nothing
        End Get
    End Property

    ''' <summary>打开投影预览。未安装则引导下载。</summary>
    Public Sub OpenSchematicViewer()
        Dim Exe = SchematicViewerExe
        If Exe IsNot Nothing Then
            Try
                Process.Start(New ProcessStartInfo With {.FileName = Exe, .UseShellExecute = True})
            Catch ex As Exception
                Hint("打开投影预览失败：" & ex.Message, HintType.Red)
            End Try
        Else
            Dim Msg = "投影预览（Litematic Viewer）还没安装。" & vbCrLf & vbCrLf &
                "1. 点「打开下载页」从小飞机网盘下载" & vbCrLf &
                "2. 解压压缩包" & vbCrLf &
                "3. 把解压出来的文件夹放到：" & vbCrLf &
                SchematicViewerDir & vbCrLf & vbCrLf &
                "放好后回来再点一次就能直接打开。"
            Dim Ok As Integer = MyMsgBox(Msg, "获取投影预览", "打开下载页", "取消")
            If Ok = 1 Then
                Process.Start(New ProcessStartInfo With {
                    .FileName = SchematicViewerShare,
                    .UseShellExecute = True})
                '通知服务端计数
                RunInNewThread(Sub()
                    Try
                        CType(Net.WebRequest.Create("https://slatcl.byethost8.com/api/index.php?action=schematic_download"), Net.HttpWebRequest).GetResponse().Dispose()
                    Catch
                    End Try
                End Sub, "SchematicDL")
            End If
        End If
    End Sub

End Module
