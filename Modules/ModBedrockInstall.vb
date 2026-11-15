Imports System
Imports System.IO
Imports System.Collections.Generic

''' <summary>
''' 基岩版安装包导入：让用户手动提供 MSIX/AppxBundle 文件，由启动器调用系统 API 安装。
''' <para>
''' <b>为什么是这个方案</b>：基岩版是 Microsoft Store 的 UWP 应用，
''' 启动器不能代替用户去商店「购买并下载」——那需要 Store 授权。
''' 但用户可以自己获取安装包（从 Store 缓存、或微软官方的离线包），
''' 然后由启动器调用 Windows 官方的 <c>Add-AppxPackage</c> 完成安装。
''' </para>
''' <para>
''' 这是微软官方支持的部署方式（企业侧载 / 离线部署都用它），
''' 不需要破解，也不绕过授权——授权仍然由用户的微软账号决定。
''' </para>
''' </summary>
Public Module ModBedrockInstall

    ''' <summary>安装结果。</summary>
    Public Class InstallResult
        Public Property Success As Boolean = False
        Public Property Message As String = ""
        Public Property PackageName As String = ""
    End Class

    ''' <summary>
    ''' 从本地 MSIX / AppxBundle / Appx 文件安装。
    ''' </summary>
    ''' <param name="FilePath">安装包完整路径。</param>
    Public Function InstallFromFile(FilePath As String) As InstallResult
        Dim R As New InstallResult
        Try
            If String.IsNullOrWhiteSpace(FilePath) OrElse Not FileUtils.Exists(FilePath) Then
                R.Message = "安装包文件不存在"
                Return R
            End If

            Dim Ext = PathUtils.GetExtension(FilePath).ToLowerInvariant()
            If Ext <> "msix" AndAlso Ext <> "appx" AndAlso Ext <> "msixbundle" AndAlso Ext <> "appxbundle" Then
                R.Message = $"不支持的文件类型：.{Ext}（需要 .msix / .appx / .msixbundle / .appxbundle）"
                Return R
            End If

            Logger.Info($"准备安装基岩版安装包：{FilePath}")

            '用 Windows 官方的 Add-AppxPackage 部署
            '注意：路径里有中文/空格，必须正确转义单引号
            Dim Escaped = FilePath.Replace("'", "''")
            Dim Script = "try {" &
                         "  Add-AppxPackage -Path '" & Escaped & "' -ErrorAction Stop;" &
                         "  Write-Output 'OK'" &
                         "} catch {" &
                         "  Write-Output ('ERR:' + $_.Exception.Message)" &
                         "}"

            Dim Out = RunPowerShell(Script)
            If Out Is Nothing Then
                R.Message = "调用系统部署接口失败"
                Return R
            End If

            Dim Trimmed = Out.Trim()
            If Trimmed.Contains("OK") Then
                R.Success = True
                R.Message = "安装完成"
                Logger.Info("基岩版安装包部署成功")

                '装完扫一次，把包名带回去
                Try
                    Dim Found = ModBedrock.GetInstalledPackages()
                    If Found.Count > 0 Then R.PackageName = Found(0).DisplayText
                Catch
                End Try
            Else
                Dim Idx = Trimmed.IndexOf("ERR:")
                R.Message = If(Idx >= 0, Trimmed.Substring(Idx + 4).Trim(), Trimmed)
                Logger.Warn($"基岩版安装失败：{R.Message}")
            End If
            Return R
        Catch ex As Exception
            Logger.Error(ex, "安装基岩版失败")
            R.Message = ex.Message
            Return R
        End Try
    End Function

    ''' <summary>
    ''' 从文件夹安装（用于 .msix 带依赖包的场景）。
    ''' </summary>
    Public Function InstallFromFolder(FolderPath As String) As InstallResult
        Dim R As New InstallResult
        Try
            If String.IsNullOrWhiteSpace(FolderPath) OrElse Not DirectoryUtils.Exists(FolderPath) Then
                R.Message = "文件夹不存在"
                Return R
            End If

            '找主包（优先 bundle）
            Dim Main As String = Nothing
            For Each Pattern In {"*.msixbundle", "*.appxbundle", "*.msix", "*.appx"}
                Dim Files = Directory.GetFiles(FolderPath, Pattern, SearchOption.TopDirectoryOnly)
                If Files.Length > 0 Then
                    Main = Files(0)
                    Exit For
                End If
            Next

            If Main Is Nothing Then
                R.Message = "文件夹里没有找到 .msix / .appx 安装包"
                Return R
            End If

            Logger.Info($"从文件夹安装，主包：{Main}")

            '依赖包一起装
            Dim Deps As New List(Of String)
            For Each Pattern In {"*.msix", "*.appx"}
                For Each F In Directory.GetFiles(FolderPath, Pattern, SearchOption.TopDirectoryOnly)
                    If F <> Main Then Deps.Add(F)
                Next
            Next

            Dim EscapedMain = Main.Replace("'", "''")
            Dim Script As String
            If Deps.Count > 0 Then
                Dim DepList = String.Join(",", Deps.ConvertAll(Function(F) "'" & F.Replace("'", "''") & "'"))
                Script = "try {" &
                         "  Add-AppxPackage -Path '" & EscapedMain & "' -DependencyPath " & DepList & " -ErrorAction Stop;" &
                         "  Write-Output 'OK'" &
                         "} catch {" &
                         "  Write-Output ('ERR:' + $_.Exception.Message)" &
                         "}"
            Else
                Script = "try {" &
                         "  Add-AppxPackage -Path '" & EscapedMain & "' -ErrorAction Stop;" &
                         "  Write-Output 'OK'" &
                         "} catch {" &
                         "  Write-Output ('ERR:' + $_.Exception.Message)" &
                         "}"
            End If

            Dim Out = RunPowerShell(Script)
            If Out Is Nothing Then
                R.Message = "调用系统部署接口失败"
                Return R
            End If

            If Out.Contains("OK") Then
                R.Success = True
                R.Message = $"安装完成（含 {Deps.Count} 个依赖）"
                Try
                    Dim Found = ModBedrock.GetInstalledPackages()
                    If Found.Count > 0 Then R.PackageName = Found(0).DisplayText
                Catch
                End Try
            Else
                Dim Idx = Out.IndexOf("ERR:")
                R.Message = If(Idx >= 0, Out.Substring(Idx + 4).Trim(), Out.Trim())
            End If
            Return R
        Catch ex As Exception
            Logger.Error(ex, "从文件夹安装基岩版失败")
            R.Message = ex.Message
            Return R
        End Try
    End Function

    ''' <summary>
    ''' 检查系统是否具备部署能力。
    ''' </summary>
    Public Function CanDeploy() As Boolean
        Try
            'Add-AppxPackage 是 Windows 10+ 内置命令
            Dim Out = RunPowerShell("if (Get-Command Add-AppxPackage -ErrorAction SilentlyContinue) { 'YES' } else { 'NO' }")
            Return Out IsNot Nothing AndAlso Out.Contains("YES")
        Catch
            Return False
        End Try
    End Function

    Private Function RunPowerShell(Script As String) As String
        Try
            Dim Psi As New Diagnostics.ProcessStartInfo With {
                .FileName = "powershell.exe",
                .Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command """ & Script.Replace("""", "\""") & """",
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .StandardOutputEncoding = Text.Encoding.UTF8
            }
            Using P As Diagnostics.Process = Diagnostics.Process.Start(Psi)
                Dim Out = P.StandardOutput.ReadToEnd()
                P.WaitForExit(180000)
                Return Out
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "执行 PowerShell 失败")
            Return Nothing
        End Try
    End Function

End Module
