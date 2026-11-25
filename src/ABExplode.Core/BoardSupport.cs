namespace ABExplode.Core;

/// <summary>Página de suporte (drivers, BIOS) de cada fabricante de placa-mãe ou de notebook, pelo nome que o DMI/WMI informa.</summary>
public static class BoardSupport
{
    /// <summary>
    /// (marca, URL) para o fabricante; ("", "") se não for reconhecido. maker/model: placa-mãe; systemMaker/systemModel:
    /// o computador (em notebook a placa-mãe vem genérica e o fabricante de verdade está aqui).
    /// </summary>
    public static (string Brand, string Url) Find(string maker, string model, string systemMaker, string systemModel)
    {
        string all = maker + " " + systemMaker;
        string q = Uri.EscapeDataString(model.Length > 0 && !model.Contains("Default", StringComparison.OrdinalIgnoreCase) ? model : systemModel);
        bool Has(string s) => all.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("ASUS")) return ("ASUS", "https://www.asus.com/support/download-center/");
        if (Has("Micro-Star") || Has("MSI")) return ("MSI", "https://www.msi.com/search/" + q);
        if (Has("GIGABYTE")) return ("Gigabyte", "https://www.gigabyte.com/Support");
        if (Has("ASRock")) return ("ASRock", "https://www.asrock.com/support/index.asp");
        if (Has("Dell")) return ("Dell", "https://www.dell.com/support/home/");
        if (Has("LENOVO")) return ("Lenovo", "https://support.lenovo.com/");
        if (Has("HP") || Has("Hewlett")) return ("HP", "https://support.hp.com/");
        if (Has("Acer")) return ("Acer", "https://www.acer.com/support");
        return ("", "");
    }
}
