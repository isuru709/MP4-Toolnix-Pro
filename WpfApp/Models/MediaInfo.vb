Imports System.Globalization
Imports System.Text.Json.Serialization

Namespace Models

    Public Class FFProbeResult
        Public Property format As FFFormat
        Public Property streams As List(Of FFStream)

        <JsonIgnore>
        Public ReadOnly Property FormatDurationSeconds As Double
            Get
                If format Is Nothing OrElse String.IsNullOrWhiteSpace(format.duration) Then Return 0
                Dim d As Double
                If Double.TryParse(format.duration, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                    Return d
                End If
                Return 0
            End Get
        End Property
    End Class

    Public Class FFFormat
        Public Property filename As String
        Public Property nb_streams As Integer
        Public Property duration As String
        Public Property size As String
        Public Property bit_rate As String
        Public Property tags As Dictionary(Of String, String)
    End Class

    Public Class FFStream
        Public Property index As Integer
        Public Property codec_name As String
        Public Property codec_type As String ' "video", "audio", "subtitle", "data"
        Public Property duration As String
        Public Property width As Integer
        Public Property height As Integer
        Public Property sample_rate As String
        Public Property channels As Integer
        Public Property channel_layout As String
        Public Property tags As Dictionary(Of String, String)
    End Class

End Namespace