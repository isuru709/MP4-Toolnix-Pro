Imports System.Diagnostics
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Globalization
Imports System.Linq
Imports MP4_Toolnix.Models
Imports MP4_Toolnix.Services

Namespace Services

    Public Class FfmpegService
        Public Property FfmpegPath As String
        Public Property FfprobePath As String

        Public Async Function ProbeAsync(inputPath As String) As Task(Of FFProbeResult)
            If String.IsNullOrWhiteSpace(inputPath) OrElse Not IO.File.Exists(inputPath) Then
                Throw New IO.FileNotFoundException("Input not found.", inputPath)
            End If
            If Not IO.File.Exists(FfprobePath) Then
                Throw New IO.FileNotFoundException("ffprobe.exe not found. Place it under .\ffmpeg\ or set FfprobePath.")
            End If

            Dim args = $"-v quiet -print_format json -show_format -show_streams ""{inputPath}"""
            Dim psi = New ProcessStartInfo(FfprobePath, args) With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .StandardOutputEncoding = Encoding.UTF8
            }

            Using p As New Process()
                p.StartInfo = psi
                Dim sb As New StringBuilder()
                AddHandler p.OutputDataReceived, Sub(s, e)
                                                     If e.Data IsNot Nothing Then
                                                         SyncLock sb
                                                             sb.AppendLine(e.Data)
                                                         End SyncLock
                                                     End If
                                                 End Sub
                AddHandler p.ErrorDataReceived, Sub(s, e) ' ignore
                                                End Sub
                p.Start()
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()
                Await Task.Run(Sub() p.WaitForExit())
                Dim json As String
                SyncLock sb
                    json = sb.ToString()
                End SyncLock
                If p.ExitCode <> 0 OrElse String.IsNullOrWhiteSpace(json) Then
                    Throw New Exception("ffprobe failed.")
                End If
                Dim res = JsonSerializer.Deserialize(Of FFProbeResult)(json, New JsonSerializerOptions With {
                    .PropertyNameCaseInsensitive = True
                })
                Return res
            End Using
        End Function

        Public Async Function RemuxAsync(inputPath As String,
                                         selectedStreamIndices As IEnumerable(Of Integer),
                                         outputPath As String,
                                         totalDurationSeconds As Double,
                                         progress As IProgress(Of Double),
                                         ct As CancellationToken) As Task
            Dim mapArgs = String.Join(" ", selectedStreamIndices.Select(Function(i) $"-map 0:{i}"))
            Await RemuxCoreAsync(
                inputArgs:=$"-i ""{inputPath}""",
                mapArgs:=mapArgs,
                codecArgs:="-c:v copy -c:a copy -c:s mov_text",
                outputPath:=outputPath,
                totalDurationSeconds:=totalDurationSeconds,
                progress:=progress,
                ct:=ct)
        End Function

        ' Updated: strongly-typed selectedMaps
        Public Async Function RemuxMultiAsync(inputPaths As IList(Of String),
                                              selectedMaps As IList(Of StreamMap),
                                              outputPath As String,
                                              totalDurationSeconds As Double,
                                              progress As IProgress(Of Double),
                                              ct As CancellationToken) As Task
            If inputPaths Is Nothing OrElse inputPaths.Count = 0 Then
                Throw New ArgumentException("No inputs provided.", NameOf(inputPaths))
            End If
            For Each p In inputPaths
                If Not IO.File.Exists(p) Then Throw New IO.FileNotFoundException("Input not found.", p)
            Next
            If Not IO.File.Exists(FfmpegPath) Then
                Throw New IO.FileNotFoundException("ffmpeg.exe not found. Place it under .\ffmpeg\ or set FfmpegPath.")
            End If
            If IO.File.Exists(outputPath) Then
                Try : IO.File.Delete(outputPath) : Catch : End Try
            End If

            Dim inArgs As New StringBuilder()
            For Each p In inputPaths
                inArgs.Append($" -i ""{p}""")
            Next

            Dim mapArgs As String = String.Join(" ", selectedMaps.Select(Function(m) $"-map {m.Input}:{m.Stream}"))

            Dim codecArgs As String = "-c:v copy -c:a copy -c:s mov_text"

            Await RemuxCoreAsync(
                inputArgs:=inArgs.ToString().Trim(),
                mapArgs:=mapArgs,
                codecArgs:=codecArgs,
                outputPath:=outputPath,
                totalDurationSeconds:=totalDurationSeconds,
                progress:=progress,
                ct:=ct)
        End Function

        Private Async Function RemuxCoreAsync(inputArgs As String,
                                              mapArgs As String,
                                              codecArgs As String,
                                              outputPath As String,
                                              totalDurationSeconds As Double,
                                              progress As IProgress(Of Double),
                                              ct As CancellationToken) As Task
            Dim args = $"-y {inputArgs} {mapArgs} {codecArgs} -progress pipe:1 -nostats -loglevel error ""{outputPath}"""

            Dim psi = New ProcessStartInfo(FfmpegPath, args) With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .ErrorDialog = False
            }

            Using p As New Process()
                p.StartInfo = psi

                AddHandler p.OutputDataReceived, Sub(s, e)
                                                     If e.Data Is Nothing Then Return
                                                     If e.Data.StartsWith("out_time_ms=") Then
                                                         Dim v = e.Data.Substring("out_time_ms=".Length)
                                                         Dim us As Long
                                                         If Long.TryParse(v, us) AndAlso totalDurationSeconds > 0 Then
                                                             Dim sec = us / 1000000.0
                                                             Dim pct = Math.Min(100.0, Math.Max(0.0, 100.0 * sec / totalDurationSeconds))
                                                             progress?.Report(pct)
                                                         End If
                                                     End If
                                                 End Sub

                Dim errSb As New StringBuilder()
                AddHandler p.ErrorDataReceived, Sub(s, e)
                                                    If e.Data IsNot Nothing Then
                                                        SyncLock errSb
                                                            errSb.AppendLine(e.Data)
                                                        End SyncLock
                                                    End If
                                                End Sub

                p.Start()
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()

                Using ct.Register(Sub()
                                      Try
                                          If Not p.HasExited Then p.Kill(True)
                                      Catch
                                      End Try
                                  End Sub)
                    Await Task.Run(Sub() p.WaitForExit(), ct)
                End Using

                If p.ExitCode <> 0 Then
                    Dim err As String
                    SyncLock errSb
                        err = errSb.ToString()
                    End SyncLock
                    Throw New Exception($"ffmpeg failed (exit {p.ExitCode}).{Environment.NewLine}{err}")
                End If
            End Using
        End Function

    End Class

End Namespace