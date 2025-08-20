Imports Microsoft.Win32
Imports System.ComponentModel
Imports System.Globalization
Imports System.Threading
Imports System.Linq
Imports System.Windows
Imports MP4_Toolnix.Services

Namespace MP4_Toolnix

    Partial Public Class MainWindow
        Inherits System.Windows.Window

        Private _svc As New Global.MP4_Toolnix.Services.FfmpegService()
        Private _cts As CancellationTokenSource

        Private ReadOnly _inputs As New List(Of InputEntry)

        Public Sub New()
            InitializeComponent()

            Dim baseDir = AppDomain.CurrentDomain.BaseDirectory
            _svc.FfmpegPath = System.IO.Path.Combine(baseDir, "ffmpeg.exe")
            _svc.FfprobePath = System.IO.Path.Combine(baseDir, "ffprobe.exe")
        End Sub

        Private Sub AddInputs_Click(sender As Object, e As RoutedEventArgs)
            Dim dlg As New OpenFileDialog() With {
                .Filter = "Media/Subtitles|*.mp4;*.mkv;*.mov;*.avi;*.ts;*.m4a;*.aac;*.h264;*.hevc;*.ac3;*.wav;*.srt;*.ass;*.vtt|All files|*.*",
                .Multiselect = True
            }
            If dlg.ShowDialog(Me) = True Then
                For Each f In dlg.FileNames
                    If Not _inputs.Any(Function(x) String.Equals(x.Path, f, StringComparison.OrdinalIgnoreCase)) Then
                        _inputs.Add(New InputEntry With {.Path = f})
                    End If
                Next
                RefreshInputsList()
                If String.IsNullOrWhiteSpace(TxtOutput.Text) AndAlso dlg.FileNames.Length > 0 Then
                    TxtOutput.Text = System.IO.Path.ChangeExtension(dlg.FileNames(0), ".mp4")
                End If
            End If
        End Sub

        Private Sub RemoveInputs_Click(sender As Object, e As RoutedEventArgs)
            Dim sel = LstInputs.SelectedItems.Cast(Of String)().ToList()
            If sel.Count = 0 Then Return
            _inputs.RemoveAll(Function(x) sel.Contains(x.Path))
            RefreshInputsList()
            ' Also clear streams referring to removed inputs
            ListStreams.Items.Clear()
        End Sub

        Private Sub RefreshInputsList()
            LstInputs.Items.Clear()
            For Each i In _inputs
                LstInputs.Items.Add(i.Path)
            Next
        End Sub

        Private Async Sub Probe_Click(sender As Object, e As RoutedEventArgs)
            If _inputs.Count = 0 Then
                MessageBox.Show(Me, "Add one or more input files first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If

            Try
                LblStatus.Text = "Probing..."
                ListStreams.Items.Clear()

                ' Probe each input sequentially (simpler error handling)
                For i = 0 To _inputs.Count - 1
                    Dim entry = _inputs(i)
                    entry.Probe = Await _svc.ProbeAsync(entry.Path)

                    Dim fileName = System.IO.Path.GetFileName(entry.Path)
                    Dim durFmt As String = ""
                    Dim totalDur As Double = 0
                    If entry.Probe IsNot Nothing Then
                        totalDur = entry.Probe.FormatDurationSeconds
                        If totalDur > 0 Then
                            durFmt = TimeSpan.FromSeconds(totalDur).ToString("hh\:mm\:ss")
                        End If
                    End If

                    For Each s In entry.Probe.streams
                        Dim lang As String = Nothing
                        If s.tags IsNot Nothing Then s.tags.TryGetValue("language", lang)
                        Dim title As String = Nothing
                        If s.tags IsNot Nothing Then s.tags.TryGetValue("title", title)

                        Dim sd As String = ""
                        If s.duration IsNot Nothing Then
                            Dim d As Double
                            If Double.TryParse(s.duration, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                                sd = TimeSpan.FromSeconds(d).ToString("hh\:mm\:ss")
                            End If
                        End If

                        ListStreams.Items.Add(New StreamRow With {
                            .Keep = (s.codec_type = "video" OrElse s.codec_type = "audio" OrElse s.codec_type = "subtitle"),
                            .Input = i,
                            .Index = s.index,
                            .Type = s.codec_type,
                            .Codec = s.codec_name,
                            .Language = If(lang, ""),
                            .Title = If(title, ""),
                            .Duration = If(sd, If(durFmt, "")),
                            .FileName = fileName
                        })
                    Next
                Next

                ' Show overall max duration
                Dim maxDur = _inputs.Where(Function(x) x.Probe IsNot Nothing).Select(Function(x) x.Probe.FormatDurationSeconds).DefaultIfEmpty(0).Max()
                If maxDur > 0 Then
                    LblStatus.Text = $"Duration (max): {TimeSpan.FromSeconds(maxDur):hh\:mm\:ss}"
                Else
                    LblStatus.Text = "Probe complete"
                End If
            Catch ex As Exception
                LblStatus.Text = "Probe failed"
                MessageBox.Show(Me, ex.Message, "Probe Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        Private Sub BrowseOutput_Click(sender As Object, e As RoutedEventArgs)
            Dim dlg As New SaveFileDialog() With {
                .Filter = "MP4 file|*.mp4|All files|*.*",
                .FileName = TxtOutput.Text
            }
            If dlg.ShowDialog(Me) = True Then
                TxtOutput.Text = dlg.FileName
            End If
        End Sub

        Private Async Sub Remux_Click(sender As Object, e As RoutedEventArgs)
            If _inputs.Count = 0 Then
                MessageBox.Show(Me, "Add input files and probe first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If
            If ListStreams.Items.Count = 0 Then
                MessageBox.Show(Me, "Probe inputs and select streams to keep.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If
            Dim maps As List(Of StreamMap) =
                ListStreams.Items.Cast(Of StreamRow)().
                Where(Function(r) r.Keep).
                Select(Function(r) New StreamMap With {.Input = r.Input, .Stream = r.Index, .Type = r.Type}).
                ToList()

            If maps.Count = 0 Then
                MessageBox.Show(Me, "Select at least one stream to keep.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If
            If String.IsNullOrWhiteSpace(TxtOutput.Text) Then
                MessageBox.Show(Me, "Choose an output file.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If

            BtnRemux.IsEnabled = False
            Progress.Value = 0
            LblStatus.Text = "Remuxing..."
            _cts = New CancellationTokenSource()
            Dim progressHandler As IProgress(Of Double) = New Global.System.Progress(Of Double)(
                Sub(p)
                    Progress.Value = p
                    LblStatus.Text = $"Remuxing... {p:F1}%"
                End Sub)

            Try
                Dim inputs = _inputs.Select(Function(x) x.Path).ToList()
                Dim totalDur = _inputs.Where(Function(x) x.Probe IsNot Nothing).
                    Select(Function(x) x.Probe.FormatDurationSeconds).DefaultIfEmpty(0).Max()

                Await _svc.RemuxMultiAsync(
                    inputPaths:=inputs,
                    selectedMaps:=maps,
                    outputPath:=TxtOutput.Text,
                    totalDurationSeconds:=totalDur,
                    progress:=progressHandler,
                    ct:=_cts.Token
                )
                Progress.Value = 100
                LblStatus.Text = "Done"
            Catch ex As OperationCanceledException
                LblStatus.Text = "Canceled"
            Catch ex As Exception
                LblStatus.Text = "Failed"
                MessageBox.Show(Me, ex.Message, "Remux Error", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                BtnRemux.IsEnabled = True
                _cts = Nothing
            End Try
        End Sub

        Protected Overrides Sub OnClosing(e As CancelEventArgs)
            If _cts IsNot Nothing Then
                _cts.Cancel()
            End If
            MyBase.OnClosing(e)
        End Sub

        Private Class InputEntry
            Public Property Path As String
            Public Property Probe As Models.FFProbeResult
        End Class

        Private Class StreamRow
            Public Property Keep As Boolean
            Public Property Input As Integer
            Public Property Index As Integer
            Public Property Type As String
            Public Property Codec As String
            Public Property Language As String
            Public Property Title As String
            Public Property Duration As String
            Public Property FileName As String
        End Class

    End Class

End Namespace