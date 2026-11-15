''' <summary>
''' 托管外部启动的服务器进程。
'''
''' Windows 不允许事后接管别的进程的标准输入输出，所以「托管」不是去抢 stdin/stdout，
''' 而是三条腿：
'''   1. WMI 扫描运行中的 java 进程，从命令行认出 -jar xxx.jar（自动搜索）；
'''   2. 命令行里 jar 是相对路径时，读进程 PEB 拿真实工作目录，才能定位 server.properties；
'''   3. 命令走 RCON 协议（server.properties 的 enable-rcon / rcon.port / rcon.password），
'''      输出走 logs\latest.log 的增量跟踪。没开 RCON 的服务器只给日志，不给命令，
'''      并明确告诉玩家怎么开，不装作能用。
''' </summary>
Imports System.Collections.Generic

Public Module ModAttach

    ''' <summary>扫描到一个可托管的外部服务器。</summary>
    Public Class AttachServer
        Public Property PID As Integer = 0
        Public Property JarPath As String = ""
        Public Property Dir As String = ""
        Public Property RconEnabled As Boolean = False
        Public Property RconPort As Integer = 25575
        Public Property RconPassword As String = ""
        Public Property ServerPort As Integer = 25565
        Public Property CommandLine As String = ""

        Public ReadOnly Property Describe As String
            Get
                Dim Name As String = IO.Path.GetFileNameWithoutExtension(JarPath)
                Return $"PID {PID} · {Name} · {Dir}"
            End Get
        End Property
    End Class

    ''' <summary>扫描所有疑似 Minecraft 服务端的 java 进程。</summary>
    Public Function ScanExternal() As List(Of AttachServer)
        Dim Out As New List(Of AttachServer)
        Try
            Dim Query As New System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, Name FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'")
            For Each Obj As System.Management.ManagementObject In Query.Get()
                Try
                    Dim Cmd As String = If(Obj("CommandLine"), "").ToString()
                    Dim PID As Integer = CInt(Obj("ProcessId"))
                    Dim Jar As String = ParseJarPath(Cmd)
                    If String.IsNullOrWhiteSpace(Jar) Then Continue For

                    If Not IO.Path.IsPathRooted(Jar) Then
                        Dim Cwd As String = ReadCwdViaPeb(PID)
                        If String.IsNullOrWhiteSpace(Cwd) Then
                            Jar = ""  '工作目录拿不到就定位不了 server.properties，不硬猜
                        Else
                            Jar = IO.Path.Combine(Cwd, Jar)
                        End If
                    End If
                    If String.IsNullOrWhiteSpace(Jar) OrElse Not IO.File.Exists(Jar) Then Continue For

                    Dim Dir As String = IO.Path.GetDirectoryName(Jar)
                    Dim A As New AttachServer With {
                        .PID = PID, .JarPath = Jar, .Dir = Dir, .CommandLine = Cmd}
                    ReadRconSettings(A)
                    Out.Add(A)
                Catch ex As Exception
                    Logger.Warn(ex, "解析 java 进程失败")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, "扫描外部服务端进程失败")
        End Try
        Return Out
    End Function

    ''' <summary>从命令行里取 -jar 后面的那个参数。纯函数，便于单测。</summary>
    Public Function ParseJarPath(CommandLine As String) As String
        If String.IsNullOrWhiteSpace(CommandLine) Then Return ""
        Dim Parts = CommandLine.Split(" "c)
        For i = 0 To Parts.Length - 2
            If String.Compare(Parts(i), "-jar", StringComparison.OrdinalIgnoreCase) = 0 Then
                Dim Jar = Parts(i + 1).Trim(""""c)
                If Jar.ToLowerInvariant().EndsWith(".jar") Then Return Jar
            End If
        Next
        Return ""
    End Function

    ''' <summary>读 server.properties 里的 RCON 配置。文件读不到就保持默认（RCON 关）。</summary>
    Public Sub ReadRconSettings(A As AttachServer)
        Try
            Dim P = IO.Path.Combine(A.Dir, "server.properties")
            If Not IO.File.Exists(P) Then Return
            For Each Line In IO.File.ReadAllLines(P)
                Dim Eq = Line.IndexOf("="c)
                If Eq <= 0 Then Continue For
                Dim Key = Line.Substring(0, Eq).Trim()
                Dim Val = Line.Substring(Eq + 1).Trim()
                Select Case Key
                    Case "enable-rcon" : A.RconEnabled = (Val = "true")
                    Case "rcon.port" : A.RconPort = CInt(Val)
                    Case "rcon.password" : A.RconPassword = Val
                    Case "server-port" : A.ServerPort = CInt(Val)
                End Select
            Next
        Catch ex As Exception
            Logger.Warn(ex, "读取外部服务端 RCON 配置失败")
        End Try
    End Sub

    '==== PEB 读工作目录 ====
    '命令行写相对路径（-jar server.jar）时，只有进程 PEB 里有真实工作目录。
    'x64 进程按 x64 布局读；32 位进程直接放弃，不做跨位数读取。

    <Runtime.InteropServices.DllImport("ntdll.dll")>
    Private Function NtQueryInformationProcess(ProcessHandle As IntPtr, ClassCode As Integer,
        ByRef PBI As PROCESS_BASIC_INFORMATION, Length As Integer, ByRef ReturnLength As Integer) As Integer
    End Function

    <Runtime.InteropServices.DllImport("kernel32.dll")>
    Private Function OpenProcess(Access As UInteger, Inherit As Boolean, PID As Integer) As IntPtr
    End Function

    <Runtime.InteropServices.DllImport("kernel32.dll")>
    Private Function ReadProcessMemory(Process As IntPtr, BaseAddress As IntPtr, Buffer As Byte(), Size As Integer, ByRef Read As Integer) As Boolean
    End Function

    <Runtime.InteropServices.DllImport("kernel32.dll")>
    Private Function CloseHandle(Handle As IntPtr) As Boolean
    End Function

    Private Structure PROCESS_BASIC_INFORMATION
        Public Reserved1 As IntPtr
        Public PebBaseAddress As IntPtr
        Public Reserved2 As IntPtr
        Public Reserved3 As IntPtr
        Public UniqueProcessId As IntPtr
        Public MoreReserved As IntPtr
    End Structure

    Private Const PROCESS_QUERY_INFORMATION As UInteger = &H400UI
    Private Const PROCESS_VM_READ As UInteger = &H10UI

    ''' <summary>读目标进程的工作目录。失败返回空串（调用方自行降级）。</summary>
    Public Function ReadCwdViaPeb(PID As Integer) As String
        Dim Proc As IntPtr = IntPtr.Zero
        Try
            Proc = OpenProcess(PROCESS_QUERY_INFORMATION Or PROCESS_VM_READ, False, PID)
            If Proc = IntPtr.Zero Then Return ""

            Dim PBI As New PROCESS_BASIC_INFORMATION
            Dim RetLen As Integer = 0
            If NtQueryInformationProcess(Proc, 0, PBI, Runtime.InteropServices.Marshal.SizeOf(PBI), RetLen) <> 0 Then Return ""

            Dim PtrSize = Runtime.InteropServices.Marshal.SizeOf(GetType(IntPtr))
            Dim Buf(PtrSize - 1) As Byte
            Dim Read As Integer = 0

            'PEB + 0x20 = ProcessParameters（x64）
            If Not ReadProcessMemory(Proc, PBI.PebBaseAddress + &H20, Buf, PtrSize, Read) Then Return ""
            Dim Params = BitConverter.ToInt64(Buf, 0)
            If Params = 0 Then Return ""

            'ProcessParameters + 0x38 = CurrentDirectory.DosPath（UNICODE_STRING：2 字节长 + 2 字节上限 + 4 填充 + 8 指针）
            Dim StrBuf(15) As Byte
            If Not ReadProcessMemory(Proc, New IntPtr(Params + &H38), StrBuf, 16, Read) Then Return ""
            Dim StrLen = BitConverter.ToUInt16(StrBuf, 0)
            If StrLen = 0 OrElse StrLen > 1024 Then Return ""
            Dim StrPtr = BitConverter.ToInt64(StrBuf, 8)

            Dim TextBuf(StrLen - 1) As Byte
            If Not ReadProcessMemory(Proc, New IntPtr(StrPtr), TextBuf, StrLen, Read) Then Return ""
            Return System.Text.Encoding.Unicode.GetString(TextBuf)
        Catch ex As Exception
            Logger.Warn(ex, "读进程工作目录失败")
            Return ""
        Finally
            If Proc <> IntPtr.Zero Then CloseHandle(Proc)
        End Try
    End Function

    '==== RCON 客户端 ====
    'Source RCON 协议：包 = 4 字节小端长度 + 4 字节 id + 4 字节 type + 正文 + 两个 0。
    'type：3=认证，2=执行命令；服务器回 2=认证结果，0=命令输出。

    ''' <summary>一条 RCON 包：id + 类型 + 正文。</summary>
    Public Structure RconPacket
        Public ID As Integer
        Public Type As Integer
        Public Body As String
    End Structure

    Public Class RconClient
        Implements IDisposable

        Private Client As Net.Sockets.TcpClient
        Private Stream As Net.Sockets.NetworkStream
        Public ReadOnly Property Connected As Boolean
            Get
                Return Client IsNot Nothing AndAlso Client.Connected
            End Get
        End Property

        ''' <summary>连接并认证。认证失败抛异常，调用方把原文给玩家看。</summary>
        Public Sub Connect(Host As String, Port As Integer, Password As String)
            Client = New Net.Sockets.TcpClient()
            Dim ConnectTask = Client.ConnectAsync(Host, Port)
            If Not ConnectTask.Wait(6000) Then
                Throw New Exception("RCON 连接超时（" & Host & ":" & Port & "）")
            End If
            Stream = Client.GetStream()
            Stream.ReadTimeout = 8000

            SendPacket(1, 3, Password)
            Dim P = ReadPacket()
            '有的服务器先发一条 type 0 的 MOTD 再回认证结果，这里滤掉
            While P.Type <> 2
                P = ReadPacket()
            End While
            If P.ID = -1 Then
                Disconnect()
                Throw New Exception("RCON 认证失败：rcon.password 不对")
            End If
        End Sub

        Public Function Exec(Command As String) As String
            If Not Connected Then Throw New Exception("RCON 已断开")
            Dim Id = New Random().Next(2, Integer.MaxValue)
            SendPacket(Id, 2, Command)
            Dim P = ReadPacket()
            Return P.Body
        End Function

        Public Sub Disconnect()
            Try
                If Stream IsNot Nothing Then Stream.Dispose()
                If Client IsNot Nothing Then Client.Dispose()
            Catch
            End Try
            Stream = Nothing
            Client = Nothing
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Disconnect()
        End Sub

        '—— 包的编解码拆成共享纯函数，便于单测 ——

        ''' <summary>往字节数组指定偏移写一个 32 位小端整数。</summary>
        Public Shared Sub WriteInt(Buf As Byte(), Offset As Integer, Value As Integer)
            Dim B = BitConverter.GetBytes(Value)
            Buf(Offset) = B(0) : Buf(Offset + 1) = B(1) : Buf(Offset + 2) = B(2) : Buf(Offset + 3) = B(3)
        End Sub

        Public Shared Function EncodePacket(ID As Integer, Type As Integer, Body As String) As Byte()
            Dim Payload = System.Text.Encoding.UTF8.GetBytes(Body)
            Dim Length = 4 + 4 + Payload.Length + 2
            Dim Buf(3 + Length) As Byte
            WriteInt(Buf, 0, Length)
            WriteInt(Buf, 4, ID)
            WriteInt(Buf, 8, Type)
            Array.Copy(Payload, 0, Buf, 12, Payload.Length)
            '末尾两个 0 已由数组默认值保证
            Return Buf
        End Function

        ''' <summary>解析「去掉 4 字节长度前缀」的包体：id@0 / type@4 / body@8。</summary>
        Public Shared Function DecodePacket(Buf As Byte()) As RconPacket
            If Buf.Length < 10 Then Return New RconPacket With {.ID = 0, .Type = 0, .Body = ""}
            Dim P As New RconPacket
            P.ID = BitConverter.ToInt32(Buf, 0)
            P.Type = BitConverter.ToInt32(Buf, 4)
            Dim BodyLen = Buf.Length - 10
            If BodyLen < 0 Then BodyLen = 0
            P.Body = System.Text.Encoding.UTF8.GetString(Buf, 8, BodyLen)
            Return P
        End Function

        Private Sub SendPacket(ID As Integer, Type As Integer, Body As String)
            Dim Buf = EncodePacket(ID, Type, Body)
            Stream.Write(Buf, 0, Buf.Length)
            Stream.Flush()
        End Sub

        Private Function ReadPacket() As RconPacket
            Dim Head(3) As Byte
            ReadFull(Head, 4)
            Dim Length = BitConverter.ToInt32(Head, 0)
            If Length < 10 OrElse Length > 8192 Then Throw New Exception("RCON 响应异常")
            Dim Buf(Length - 1) As Byte
            ReadFull(Buf, Length)
            Return DecodePacket(Buf)
        End Function

        Private Sub ReadFull(Buf As Byte(), Total As Integer)
            Dim Got = 0
            While Got < Total
                Dim N = Stream.Read(Buf, Got, Total - Got)
                If N <= 0 Then Throw New Exception("RCON 连接被服务器关闭")
                Got += N
            End While
        End Sub
    End Class

    ''' <summary>
    ''' latest.log 增量跟踪：每秒读一次新增字节。Minecraft 服务端会持续往里写，
    ''' 所以即使拿不到实时 stdout，也能把控制台输出完整显示出来。
    ''' </summary>
    Public Class LogTail
        Private LogPath As String
        Private LastLength As Long = 0
        Private Rest As String = ""

        Public Sub New(Path As String)
            LogPath = Path
            Try
                If IO.File.Exists(LogPath) Then LastLength = New IO.FileInfo(LogPath).Length
            Catch
            End Try
        End Sub

        ''' <summary>取自上次调用以来的新增行。没有新内容返回空列表。</summary>
        Public Function Poll() As List(Of String)
            Dim Out As New List(Of String)
            Try
                If Not IO.File.Exists(LogPath) Then Return Out
                Using Fs As New IO.FileStream(LogPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
                    If Fs.Length <= LastLength Then
                        '日志被轮换（文件变小）时从头读
                        If Fs.Length < LastLength Then LastLength = 0 Else Return Out
                    End If
                    Fs.Seek(LastLength, IO.SeekOrigin.Begin)
                    Dim Left = CInt(Fs.Length - LastLength)
                    Dim Buf(Left - 1) As Byte
                    Dim Got = 0
                    While Got < Left
                        Dim N = Fs.Read(Buf, Got, Left - Got)
                        If N <= 0 Then Exit While
                        Got += N
                    End While
                    LastLength += Got
                    Dim Text = Rest + System.Text.Encoding.UTF8.GetString(Buf, 0, Got)
                    Dim Lines = Text.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                    '最后一段可能是不完整的行，留到下次
                    If Text.Length > 0 AndAlso Not Text.EndsWith(vbLf) AndAlso Lines.Length > 0 Then
                        Rest = Lines(Lines.Length - 1)
                        If Lines.Length > 0 Then Lines(Lines.Length - 1) = Nothing
                    End If
                    For Each L In Lines
                        If L IsNot Nothing AndAlso L.Length > 0 Then Out.Add(L)
                    Next
                End Using
            Catch ex As Exception
                Logger.Warn(ex, "跟踪服务端日志失败")
            End Try
            Return Out
        End Function
    End Class
End Module
