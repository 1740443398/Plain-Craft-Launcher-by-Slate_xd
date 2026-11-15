Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq

''' <summary>
''' 插件广场：从 GitHub 发现并安装社区插件。
''' <para>
''' <b>为什么不建自己的服务器</b>：插件本质是代码，最可信的来源是代码托管平台本身——
''' 谁写的、什么时候改的、有多少人 star，这些都是公开可查的。
''' 自建服务器反而要求用户信任我们，而且还要维护。
''' </para>
''' <para>
''' <b>为什么不只靠 topic</b>：topic 是任何人都能打的标签。
''' 只要搜索 topic:pcl-plugin，随便谁给仓库打上这个词就会出现在广场里，
''' 包括那些根本不是给本启动器写的项目。这不是我们能控制的。
''' </para>
''' <para>
''' <b>现在的做法：白名单校验</b>。topic 只用来「发现候选」，
''' 是否真正收录由仓库里有没有官方模板标记文件 <see cref="TemplateMarker"/> 决定。
''' 也就是说：必须从官方模板仓库创建（Use this template 或 fork），
''' 才会带上这个标记文件，才可能被收录。
''' </para>
''' <para>
''' <b>为什么不直接用 GitHub 的 fork 关系</b>：用 Use this template 建的仓库
''' 不是 fork，API 的 parent 字段是空的，这种最主流的用法会被漏掉；
''' 而写进仓库的文件内容两种方式都会保留。所以标记文件覆盖面更全。
''' </para>
''' <para>
''' <b>这是筛选，不是沙箱</b>：标记文件不能证明作者是好人，
''' 它只证明这个仓库是照着我们的模板起的。真正的安全边界仍然是
''' <see cref="ModPluginAudit"/>：下载后立刻静态审计，危险内容直接拒绝并删除。
''' </para>
''' <para>
''' <b>连标记文件都能被伪造吗</b>：能。任何人手抄一份 .pcl-plugin.json 就能通过这一关。
''' 这一层解决的是「不相干的项目被动出现在我们的广场里」——
''' 那类作者压根不知道有这个标记，也就不会去抄；而刻意伪造的人，
''' 无论如何都会写恶意代码，拦住他们靠的是审计，不是这张名单。
''' 换句话说：白名单负责「宁缺毋滥」，审计负责「有也不怕」。
''' </para>
''' </summary>
Public Module ModPluginStore

    ''' <summary>收录约定：GitHub 仓库需要打的 topic（仅用于发现候选）。</summary>
    Public Const Topic As String = "pcl-plugin"
    ''' <summary>兼容的第二个 topic。</summary>
    Public Const TopicAlt As String = "plaincraftlauncher-plugin"

    ''' <summary>
    ''' 官方插件模板仓库全名（owner/name）。白名单校验的唯一基准。
    ''' 模板仓库自己也要带标记文件，所以它自己会被收录。
    ''' </summary>
    Public Const OfficialTemplate As String = "1740443398/pcl-plugin-template"

    ''' <summary>
    ''' 模板标记文件名。由模板仓库提供，随 Use this template / fork 一起复制。
    ''' 内容必须含 <c>"template": "1740443398/pcl-plugin-template"</c>。
    ''' </summary>
    Public Const TemplateMarker As String = ".pcl-plugin.json"

    ''' <summary>标记文件的 spec 前缀，用于校验文件结构确实是我们的模板生成的。</summary>
    Public Const TemplateSpecPrefix As String = "pcl-plugin-template/"

    ''' <summary>官方插件模板仓库地址。新人从这里开始最快。</summary>
    Public Const TemplateRepo As String = "https://github.com/" & OfficialTemplate

    ''' <summary>单次校验最多探测多少个候选仓库，避免 topic 泛滥时把启动器拖住。</summary>
    Private Const MaxProbe As Integer = 30

    ''' <summary>候选仓库校验的并发数。</summary>
    Private Const ProbeConcurrency As Integer = 6

    ''' <summary>单次标记文件请求的超时（毫秒）。探测是并发的，短超时才能整体可控。</summary>
    Private Const ProbeTimeout As Integer = 5000

    ''' <summary>直连 raw.githubusercontent.com 的超时。国内基本必挂，给短一点的特权。</summary>
    Private Const ProbeDirectTimeout As Integer = 3000

    ''' <summary>整个校验阶段的总时间预算（毫秒）。超了就带着已确认的结果返回。</summary>
    Private Const ProbeBudget As Integer = 40000

    ''' <summary>广场里的一个插件。</summary>
    Public Class StoreEntry
        Public Property RepoFullName As String = ""
        Public Property Name As String = ""
        Public Property Description As String = ""
        Public Property Author As String = ""
        Public Property Homepage As String = ""
        Public Property Stars As Integer = 0
        Public Property UpdatedAt As String = ""
        Public Property DefaultBranch As String = ""
        ''' <summary>清单里声明的最低版本要求（若仓库有 plugin.json）。</summary>
        Public Property PluginId As String = ""
        Public Property PluginVersion As String = ""
        ''' <summary>模板标记文件里声明的模板版本。通过校验的仓库至少为 1。</summary>
        Public Property TemplateVersion As Integer = 0

        Public ReadOnly Property DisplayName As String
            Get
                Return If(String.IsNullOrWhiteSpace(PluginId), Name, PluginId)
            End Get
        End Property
    End Class

    ''' <summary>最近一次拉取到的广场条目。</summary>
    Public ReadOnly Entries As New List(Of StoreEntry)

    ''' <summary>
    ''' 最近一次拉取中被白名单筛掉的仓库（全名 + 原因）。
    ''' 保留下来是为了让用户和作者能自己排查为什么没被收录。
    ''' </summary>
    Public ReadOnly Skipped As New List(Of String)

    Public Property LastFetchOK As Boolean = False
    Public Property LastError As String = ""

    ''' <summary>
    ''' 从 GitHub 发现候选仓库，逐个校验模板标记，只有通过白名单的才进入 <see cref="Entries"/>。
    ''' </summary>
    ''' <returns>搜索本身是否成功。成功但筛完为空也返回 True，界面会显示空列表提示而不是报错。</returns>
    Public Function Fetch() As Boolean
        Try
            Entries.Clear()
            Skipped.Clear()

            Dim Candidates As List(Of JObject) = Nothing
            Dim SearchError As String = ""
            If Not SearchCandidates(Candidates, SearchError) Then Return Fail(SearchError)

            If Candidates.Count = 0 Then
                LastFetchOK = True
                LastError = ""
                Logger.Info("插件广场：没有找到任何打了约定 topic 的仓库")
                Return True
            End If

            'topic 泛滥时不能无限探测，按搜索返回顺序（star 降序）截断
            Dim ToProbe = Candidates
            Dim Truncated As Boolean = False
            If ToProbe.Count > MaxProbe Then
                Truncated = True
                ToProbe = ToProbe.Take(MaxProbe).ToList()
            End If

            '并发拉取标记文件：串行探测 30 个仓库最坏要三分钟，界面会像卡死
            Dim Probes(ToProbe.Count - 1) As TemplateMarkerInfo
            Dim Deadline As Long = GetTimeMs() + ProbeBudget
            Try
                Dim Opts As New System.Threading.Tasks.ParallelOptions With {
                    .MaxDegreeOfParallelism = ProbeConcurrency
                }
                System.Threading.Tasks.Parallel.For(0, ToProbe.Count, Opts,
                    Sub(I)
                        '模板仓库自己不需要探测：它就是白名单的定义，
                        '而且探测要走国内不稳定的镜像，不该让「官方模板能否被列出」取决于网络运气
                        If IsOfficialTemplate(ToProbe(I)) Then
                            Probes(I) = OfficialTemplateMarker()
                            Return
                        End If
                        '预算用完就不再发起新请求，已经拿到的结果照常使用
                        If GetTimeMs() > Deadline Then Return
                        Try
                            Probes(I) = ProbeMarker(ToProbe(I))
                        Catch ex As Exception
                            Logger.Warn(ex, $"校验仓库模板标记失败：{ToProbe(I)("full_name")?.ToString()}")
                        End Try
                    End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "并发校验模板标记时出错")
            End Try

            Dim RejectedMissing As Integer = 0
            Dim RejectedForeign As Integer = 0
            Dim RejectedBroken As Integer = 0

            For I = 0 To ToProbe.Count - 1
                Try
                    Dim It = ToProbe(I)
                    Dim Full = It("full_name")?.ToString()
                    If String.IsNullOrWhiteSpace(Full) Then Continue For

                    Dim Marker = Probes(I)
                    If Marker Is Nothing Then
                        '没有标记文件：不是从模板创建的，跳过。
                        '探测超时/网络失败也走这里：宁可少显示，不能多显示。
                        RejectedMissing += 1
                        SkipRepo(Full, "没有 " & TemplateMarker & "，不是基于官方模板创建")
                        Continue For
                    End If

                    If Not Marker.SpecOK Then
                        RejectedBroken += 1
                        SkipRepo(Full, $"{TemplateMarker} 格式不对（缺少 {TemplateSpecPrefix}x 的 spec 字段）")
                        Continue For
                    End If

                    If Not String.Equals(Marker.Template, OfficialTemplate, StringComparison.OrdinalIgnoreCase) Then
                        '标记文件存在但指向别的模板：可能是别人自建了一套模板体系
                        RejectedForeign += 1
                        SkipRepo(Full, $"template 字段是 {If(Marker.Template, "(空)")}，不是 {OfficialTemplate}")
                        Continue For
                    End If

                    Entries.Add(New StoreEntry With {
                        .RepoFullName = Full,
                        .Name = If(It("name")?.ToString(), ""),
                        .Description = If(It("description")?.ToString(), ""),
                        .Author = If(It("owner")?("login")?.ToString(), ""),
                        .Homepage = If(It("html_url")?.ToString(), ""),
                        .Stars = If(It("stargazers_count")?.ToObject(Of Integer)(), 0),
                        .UpdatedAt = If(It("updated_at")?.ToString(), "").Split("T"c)(0),
                        .DefaultBranch = If(It("default_branch")?.ToString(), "main"),
                        .TemplateVersion = Marker.TemplateVersion
                    })
                Catch ex As Exception
                    Logger.Warn(ex, "解析广场条目失败")
                End Try
            Next

            '按 star 降序，和原来的展示顺序保持一致
            Dim Sorted = Entries.OrderByDescending(Function(E) E.Stars).ToList()
            Entries.Clear()
            Entries.AddRange(Sorted)

            LastFetchOK = True
            LastError = ""
            Logger.Info($"插件广场：候选 {ToProbe.Count} 个，模板校验通过 {Entries.Count} 个" &
                        $"，跳过 {Skipped.Count} 个（无标记 {RejectedMissing}，" &
                        $"标记指向别的模板 {RejectedForeign}，标记格式错误 {RejectedBroken}）" &
                        If(Truncated, $"；候选超过 {MaxProbe} 个，只校验了前 {MaxProbe} 个", ""))
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "拉取插件广场失败")
            Return Fail(ex.Message)
        End Try
    End Function

    ''' <summary>记录一个被白名单筛掉的仓库，同时写进日志。</summary>
    Private Sub SkipRepo(Full As String, Reason As String)
        Try
            If Skipped.Count < 200 Then Skipped.Add($"{Full}：{Reason}")
            Logger.Info($"插件广场跳过 {Full}：{Reason}")
        Catch
        End Try
    End Sub

    ''' <summary>这个候选是不是官方模板仓库自己。</summary>
    Private Function IsOfficialTemplate(Item As JObject) As Boolean
        Try
            Dim Full = Item?("full_name")?.ToString()
            Return Not String.IsNullOrWhiteSpace(Full) AndAlso
                   String.Equals(Full, OfficialTemplate, StringComparison.OrdinalIgnoreCase)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 官方模板仓库的标记信息：它是白名单的基准，直接认，不必依赖网络。
    ''' <para>
    ''' 这不是给模板仓库开后门——它的地位本来就是「定义什么算合法」，
    ''' 让它自己的收录结果取决于镜像是否抽风，只会让广场莫名其妙地空掉。
    ''' plugin.json 是否存在仍然由 <see cref="Install"/> 在安装时校验。
    ''' </para>
    ''' </summary>
    Private Function OfficialTemplateMarker() As TemplateMarkerInfo
        Return New TemplateMarkerInfo With {
            .Template = OfficialTemplate,
            .TemplateVersion = 1,
            .SpecOK = True
        }
    End Function

    ''' <summary>
    ''' 搜索打了约定 topic 的仓库，合并去重后返回原始 JSON 对象。
    ''' 只负责发现，不代表收录。
    ''' </summary>
    Private Function SearchCandidates(ByRef Out As List(Of JObject), ByRef Err As String) As Boolean
        Out = New List(Of JObject)
        Err = ""
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim AnyOK As Boolean = False
        Dim LastErr As String = ""

        For Each T In {Topic, TopicAlt}
            Dim Query = $"https://api.github.com/search/repositories?q=topic:{T}&sort=stars&order=desc&per_page=50"
            Dim Raw As String = HttpGet(Query)
            If String.IsNullOrWhiteSpace(Raw) Then
                LastErr = $"GitHub 没有响应（topic: {T}），可能是网络或频率限制"
                Continue For
            End If

            Dim Root As JToken = Nothing
            Try
                Root = JToken.Parse(Raw)
            Catch ex As Exception
                LastErr = "GitHub 返回的不是合法 JSON"
                Continue For
            End Try
            If Root("message") IsNot Nothing Then
                LastErr = $"GitHub 返回：{Root("message")}"
                Continue For
            End If

            Dim Items As JArray = TryCast(Root("items"), JArray)
            If Items Is Nothing Then
                LastErr = "返回内容格式不对"
                Continue For
            End If

            AnyOK = True
            For Each It In Items
                Dim Full = It("full_name")?.ToString()
                If String.IsNullOrWhiteSpace(Full) Then Continue For
                If Not Seen.Add(Full) Then Continue For
                Dim Obj = TryCast(It, JObject)
                If Obj IsNot Nothing Then Out.Add(Obj)
            Next
        Next

        If Not AnyOK Then
            Err = If(LastErr = "", "GitHub 搜索不可用", LastErr)
            Return False
        End If
        Return True
    End Function

    ''' <summary>模板标记文件的解析结果。</summary>
    Private Class TemplateMarkerInfo
        Public Property Template As String = ""
        Public Property TemplateVersion As Integer = 0
        ''' <summary>spec 字段是否以 <see cref="TemplateSpecPrefix"/> 开头。</summary>
        Public Property SpecOK As Boolean = False
    End Class

    ''' <summary>
    ''' 拉取并解析一个仓库的模板标记文件。拿不到或解析失败返回 Nothing。
    ''' <para>
    ''' 直连 raw.githubusercontent.com 在国内经常超时，因此失败后依次尝试
    ''' <see cref="ModGithubMirror"/> 里的加速镜像。直连永远排第一，
    ''' 镜像只是兜底——标记文件只用来决定「列不列出来」，
    ''' 真正装进来的东西仍然要过 <see cref="ModPluginAudit"/>。
    ''' </para>
    ''' <para>
    ''' 这里不用 <see cref="NetRequestByClient"/>：它禁止在 UI 线程调用，
    ''' 而探测是在后台线程跑的、且需要独立的短超时与「不写日志」行为。
    ''' 自己发一个最小的 GET，行为完全可控。
    ''' </para>
    ''' </summary>
    Private Function ProbeMarker(Item As JObject) As TemplateMarkerInfo
        Try
            Dim Full = Item?("full_name")?.ToString()
            If String.IsNullOrWhiteSpace(Full) Then Return Nothing
            Dim Branch = Item("default_branch")?.ToString()
            If String.IsNullOrWhiteSpace(Branch) Then Branch = "main"
            Branch = Branch.Trim()

            Dim RawUrl = $"https://raw.githubusercontent.com/{Full}/{Branch}/{TemplateMarker}"
            Dim Urls As New List(Of String) From {RawUrl}
            For Each Template In ModGithubMirror.GithubMirrors
                Urls.Add(String.Format(Template, RawUrl))
            Next

            Dim Text As String = Nothing
            For Each Url In Urls
                '直连给短超时：国内连 raw 基本是干等，早失败早换镜像
                Dim T = If(Url = RawUrl, ProbeDirectTimeout, ProbeTimeout)
                Text = ProbeGet(Url, T)
                If String.IsNullOrWhiteSpace(Text) Then Continue For

                '必须当场解析成功才算拿到标记文件，否则继续试下一个镜像。
                '公共加速镜像并不可靠：有的在 404 时返回 HTML，
                '有的域名已经过期，直接返回「域名出售」页面。
                '如果只看「有没有内容」就退出循环，一个坏镜像会把后面全堵死。
                Dim Root As JToken = Nothing
                Try
                    Root = JToken.Parse(Text)
                Catch
                    Continue For
                End Try
                If Root Is Nothing OrElse Root.Type <> JTokenType.Object Then Continue For
                '镜像预检页、错误 JSON 也可能解析成对象，认之前先看有没有我们的字段
                If Root("template") Is Nothing AndAlso Root("spec") Is Nothing Then Continue For
                Return ParseMarker(Root)
            Next
            Return Nothing
        Catch ex As Exception
            Logger.Warn(ex, "解析模板标记文件失败")
            Return Nothing
        End Try
    End Function

    ''' <summary>把解析好的标记文件 JSON 读成 <see cref="TemplateMarkerInfo"/>。</summary>
    Private Function ParseMarker(Root As JToken) As TemplateMarkerInfo
        Dim Info As New TemplateMarkerInfo
        Info.Template = If(Root("template")?.ToString(), "").Trim()
        Dim Ver = Root("templateVersion")
        If Ver IsNot Nothing Then
            Try
                Info.TemplateVersion = Ver.ToObject(Of Integer)()
            Catch
                Info.TemplateVersion = 0
            End Try
        End If
        Dim Spec = If(Root("spec")?.ToString(), "").Trim()
        Info.SpecOK = Spec.StartsWith(TemplateSpecPrefix, StringComparison.OrdinalIgnoreCase)
        Return Info
    End Function

    ''' <summary>
    ''' 探测专用的极简 GET：短超时、失败静默、不写日志。
    ''' 只用来判断「这个仓库有没有标记文件」，不用于下载任何要执行的东西。
    ''' </summary>
    Private Function ProbeGet(Url As String, TimeoutMs As Integer) As String
        Try
            Net.ServicePointManager.SecurityProtocol = Net.ServicePointManager.SecurityProtocol Or
                                                       Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
            Dim Req As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Req.Method = "GET"
            Req.Timeout = TimeoutMs
            Req.ReadWriteTimeout = TimeoutMs
            Req.UserAgent = "SLATCL/" & VersionBaseName
            Req.Accept = "*/*"
            Req.AutomaticDecompression = Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                If CInt(Resp.StatusCode) <> 200 Then Return Nothing
                Using Reader As New IO.StreamReader(Resp.GetResponseStream(), Text.Encoding.UTF8)
                    Dim Body = Reader.ReadToEnd()
                    '标记文件很小，超过 8KB 的一律不认，防止被塞了个大文件拖住
                    If Body Is Nothing OrElse Body.Length > 8192 Then Return Nothing
                    Return Body
                End Using
            End Using
        Catch
            '404 / 超时 / TLS 失败都走这里：没有标记文件就是没有，不打日志
            Return Nothing
        End Try
    End Function

    Private Function Fail(Msg As String) As Boolean
        LastFetchOK = False
        LastError = If(Msg, "未知错误")
        Logger.Warn($"插件广场：{LastError}")
        Return False
    End Function

    ''' <summary>
    ''' 下载一个仓库的 zip 并解压到插件目录。
    ''' </summary>
    ''' <returns>安装后的插件目录；失败返回 Nothing。</returns>
    Public Function Install(Entry As StoreEntry, Optional Loader As LoaderBase = Nothing) As String
        Try
            If Entry Is Nothing OrElse String.IsNullOrWhiteSpace(Entry.RepoFullName) Then Return Nothing

            '目录名用仓库名，冲突时加后缀
            Dim BaseName = Entry.Name
            For Each C In System.IO.Path.GetInvalidFileNameChars()
                BaseName = BaseName.Replace(C, "_"c)
            Next
            Dim TargetDir = System.IO.Path.Combine(ModPlugin.PluginDir, BaseName)
            If DirectoryUtils.Exists(TargetDir) Then TargetDir = TargetDir & "-" & Date.Now.ToString("HHmmss")

            'GitHub 的 zip 下载地址
            Dim Url = $"https://github.com/{Entry.RepoFullName}/archive/refs/heads/{Entry.DefaultBranch}.zip"
            Logger.Info($"下载插件：{Url}")
            '直连 github.com 在国内经常超时，按 镜像 > 原址 的顺序逐个试
            Dim Data As Byte() = Nothing
            For Each Candidate In ModGithubMirror.GetDownloadUrls(Url, ModGithubMirror.EnableMirror)
                Data = HttpGetBytes(Candidate, Loader)
                If Data IsNot Nothing AndAlso Data.Length >= 1024 Then Exit For
                Data = Nothing
            Next
            If Data Is Nothing OrElse Data.Length < 1024 Then
                Hint("插件下载失败或文件过小", HintType.Red)
                Return Nothing
            End If
            If Data.Length < 2 OrElse Data(0) <> &H50 OrElse Data(1) <> &H4B Then
                Hint("下载到的不是有效的 zip", HintType.Red)
                Return Nothing
            End If

            '解压到临时目录
            Dim TempDir = TargetDir & ".tmp"
            If DirectoryUtils.Exists(TempDir) Then System.IO.Directory.Delete(TempDir, True)
            DirectoryUtils.Create(TempDir)
            Try
                '先落地成临时 zip（ZipFile 需要真实路径），用完立刻删，不留在 Temp 里
                Dim TempZip As String = ToTempZip(Data)
                Try
                    System.IO.Compression.ZipFile.ExtractToDirectory(TempZip, TempDir)
                Catch ex As Exception
                    Logger.Error(ex, "解压插件失败")
                    Hint($"解压失败：{ex.Message}", HintType.Red)
                    Try
                        System.IO.Directory.Delete(TempDir, True)
                    Catch
                    End Try
                    Return Nothing
                Finally
                    '临时 zip 无论成败都要删，否则每装一次就留一份在 Temp
                    Try
                        If FileUtils.Exists(TempZip) Then FileUtils.Delete(TempZip, True)
                    Catch
                    End Try
                End Try
            Catch ex As Exception
                Logger.Error(ex, "解压插件失败")
                Hint($"解压失败：{ex.Message}", HintType.Red)
                Try
                    System.IO.Directory.Delete(TempDir, True)
                Catch
                End Try
                Return Nothing
            End Try

            'GitHub 的 zip 里有一层 仓库名-分支名 的目录，要剥掉
            Dim Inner = TempDir
            Dim Subs = System.IO.Directory.GetDirectories(TempDir)
            If Subs.Length = 1 AndAlso System.IO.Directory.GetFiles(TempDir).Length = 0 Then
                Inner = Subs(0)
            End If

            '检查清单是否存在
            If Not FileUtils.Exists(System.IO.Path.Combine(Inner, "plugin.json")) Then
                Hint("这个仓库里没有 plugin.json，不是有效的插件", HintType.Red)
                Try
                    System.IO.Directory.Delete(TempDir, True)
                Catch
                End Try
                Return Nothing
            End If

            '移动到位
            Try
                If System.IO.Directory.GetParent(TargetDir) IsNot Nothing Then
                    DirectoryUtils.Create(System.IO.Path.GetDirectoryName(TargetDir))
                End If
                System.IO.Directory.Move(Inner, TargetDir)
            Catch ex As Exception
                Logger.Error(ex, "放置插件目录失败")
                Hint($"安装失败：{ex.Message}", HintType.Red)
                Return Nothing
            Finally
                Try
                    If DirectoryUtils.Exists(TempDir) Then System.IO.Directory.Delete(TempDir, True)
                Catch
                End Try
            End Try

            '装完立刻审计
            ModPlugin.Scan()
            Dim Installed = ModPlugin.Plugins.FirstOrDefault(Function(p) p.Directory = TargetDir)
            If Installed IsNot Nothing Then
                Dim Findings = ModPluginAudit.Audit(Installed)
                If ModPluginAudit.HighestRisk(Findings) = ModPluginAudit.RiskLevel.Dangerous Then
                    '危险就删掉，不留在磁盘上
                    Try
                        System.IO.Directory.Delete(TargetDir, True)
                    Catch
                    End Try
                    ModPlugin.Scan()
                    Hint("插件含危险内容，已拒绝安装并删除", HintType.Red)
                    Logger.Warn($"拒绝安装插件 {Entry.RepoFullName}：{ModPluginAudit.Summarize(Findings)}")
                    Return Nothing
                End If
            End If

            Logger.Info($"插件已安装到 {TargetDir}")
            Hint($"插件已安装：{Entry.Name}", HintType.Green)
            Return TargetDir
        Catch ex As Exception
            Logger.Error(ex, "安装插件失败")
            Hint($"安装失败：{ex.Message}", HintType.Red)
            Return Nothing
        End Try
    End Function

    ''' <summary>把内存里的 zip 写成临时文件（ZipFile 需要路径）。</summary>
    Private Function ToTempZip(Data As Byte()) As String
        Dim P = System.IO.Path.Combine(PathTemp, "plugin-" & Guid.NewGuid().ToString("N") & ".zip")
        DirectoryUtils.Create(PathTemp)
        IO.File.WriteAllBytes(P, Data)
        Return P
    End Function

    Private Function HttpGet(Url As String) As String
        Try
            Dim Req = Prepare(Url)
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                Using Reader As New IO.StreamReader(Resp.GetResponseStream(), Text.Encoding.UTF8)
                    Return Reader.ReadToEnd()
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"请求失败：{Url}")
            Return Nothing
        End Try
    End Function

    Private Function HttpGetBytes(Url As String, Optional Loader As LoaderBase = Nothing) As Byte()
        Try
            Dim Req = Prepare(Url)
            Req.Timeout = 120000
            Req.ReadWriteTimeout = 120000
            Using Resp As Net.HttpWebResponse = CType(Req.GetResponse(), Net.HttpWebResponse)
                Dim Total = Resp.ContentLength
                Using Src = Resp.GetResponseStream()
                    Using Ms As New IO.MemoryStream()
                        Dim Buf(81919) As Byte
                        Dim Read As Integer
                        Do
                            Read = Src.Read(Buf, 0, Buf.Length)
                            If Read <= 0 Then Exit Do
                            Ms.Write(Buf, 0, Read)
                            If Loader IsNot Nothing AndAlso Total > 0 Then Loader.Progress = CDbl(Ms.Length) / Total
                        Loop
                        Return Ms.ToArray()
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"下载失败：{Url}")
            Return Nothing
        End Try
    End Function

    Private Function Prepare(Url As String) As Net.HttpWebRequest
        Net.ServicePointManager.SecurityProtocol = Net.ServicePointManager.SecurityProtocol Or
                                                   Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
        Dim Req As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
        Req.Method = "GET"
        Req.Timeout = 20000
        Req.ReadWriteTimeout = 20000
        Req.UserAgent = "SLATCL/" & VersionBaseName
        Req.Accept = "application/vnd.github+json"
        Req.AutomaticDecompression = Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate
        Return Req
    End Function

End Module
