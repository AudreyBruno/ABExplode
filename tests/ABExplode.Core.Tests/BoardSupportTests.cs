namespace ABExplode.Core.Tests;

public class BoardSupportTests
{
    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "PRIME H510M-K R2.0", "", "", "ASUS")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B550 TOMAHAWK", "", "", "MSI")]
    [InlineData("Gigabyte Technology Co., Ltd.", "B650 AORUS ELITE AX", "", "", "Gigabyte")]
    [InlineData("ASRock", "B450M Steel Legend", "", "", "ASRock")]
    // Notebook: a placa-mãe vem genérica e o fabricante de verdade está no computador
    [InlineData("", "0A1B2C", "LENOVO", "82JW", "Lenovo")]
    [InlineData("Default string", "Default string", "Dell Inc.", "Inspiron 15 3520", "Dell")]
    public void Reconhece_o_fabricante(string maker, string model, string sysMaker, string sysModel, string brand)
    {
        var (b, url) = BoardSupport.Find(maker, model, sysMaker, sysModel);
        Assert.Equal(brand, b);
        Assert.StartsWith("https://", url);
    }

    [Fact]
    public void MSI_busca_pelo_modelo_da_placa_ou_do_computador()
    {
        Assert.EndsWith("/MAG%20B550%20TOMAHAWK", BoardSupport.Find("MSI", "MAG B550 TOMAHAWK", "", "").Url);
        Assert.EndsWith("/GF63%20Thin", BoardSupport.Find("Micro-Star International Co., Ltd.", "Default string", "", "GF63 Thin").Url);
    }

    [Fact]
    public void Fabricante_desconhecido_fica_vazio()
    {
        Assert.Equal(("", ""), BoardSupport.Find("Fabricante X", "Modelo Y", "", ""));
    }
}
