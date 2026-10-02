Module Program

    Sub Main(args As String())
        Dim hoje = DateTime.Today
        Dim gerador As New GeradorRelatorio()
        gerador.Gerar(hoje.Month, hoje.Year)
    End Sub

End Module
