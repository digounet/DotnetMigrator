Imports System.Configuration
Imports System.Data.SqlClient
Imports System.IO
Imports Microsoft.Office.Interop.Excel

' Gera a planilha mensal de vendas e envia por e-mail (versão 2013, VB.NET).
Public Class GeradorRelatorio

    Private ReadOnly _conexao As String = ConfigurationManager.ConnectionStrings("Relatorios").ConnectionString

    Public Sub Gerar(mes As Integer, ano As Integer)
        Dim destino = Path.Combine("\\arquivos\relatorios", String.Format("vendas-{0}-{1}.xlsx", ano, mes))

        Using conexao As New SqlConnection(_conexao)
            conexao.Open()
            Dim comando As New SqlCommand("SELECT * FROM Vendas WHERE Mes = " & mes & " AND Ano = " & ano, conexao)
            Dim leitor = comando.ExecuteReader()

            Dim excel As New Application()
            Dim pasta = excel.Workbooks.Add()
            Dim planilha = CType(pasta.ActiveSheet, Worksheet)
            Dim linha = 1
            While leitor.Read()
                planilha.Cells(linha, 1) = leitor("Produto")
                planilha.Cells(linha, 2) = leitor("Valor")
                linha += 1
            End While
            pasta.SaveAs(destino)
            excel.Quit()
        End Using

        Using cliente As New Net.Mail.SmtpClient()
            cliente.Send("relatorios@exemplo.com.br", "diretoria@exemplo.com.br", "Vendas " & mes & "/" & ano, "Relatório em " & destino)
        End Using
        Console.WriteLine("Gerado em " & DateTime.Now)
    End Sub

End Class
