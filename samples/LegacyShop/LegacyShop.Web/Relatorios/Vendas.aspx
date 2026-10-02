<%@ Page Title="Vendas" Language="C#" AutoEventWireup="true" CodeBehind="Vendas.aspx.cs" Inherits="LegacyShop.Web.Relatorios.Vendas" %>

<!DOCTYPE html>
<html>
<head runat="server">
    <title>Relatório de vendas (legado Web Forms)</title>
</head>
<body>
    <form id="form1" runat="server">
        <asp:DropDownList ID="ddlMes" runat="server" AutoPostBack="true" OnSelectedIndexChanged="ddlMes_SelectedIndexChanged" />
        <asp:GridView ID="gvVendas" runat="server" AutoGenerateColumns="true" />
        <asp:Button ID="btnExportar" runat="server" Text="Exportar" OnClick="btnExportar_Click" />
    </form>
</body>
</html>
