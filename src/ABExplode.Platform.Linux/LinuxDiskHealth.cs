using System.Text.Json;

namespace ABExplode.Platform.Linux;

/// <summary>
/// Saúde dos discos pelo smartctl (smartmontools) em JSON, completada pelos sensores do hwmon (nvme, drivetemp).
/// O smartctl precisa de root para ler o S.M.A.R.T.; sem ele, os cartões dizem como liberar. Nada é escrito no disco.
/// </summary>
internal sealed class LinuxDiskHealth : IDiskHealth
{
    public bool IsSupported => true;

    private sealed class Raw
    {
        public int Index;
        public string Model = "", Serial = "", Firmware = "", Bus = "", Media = "", Device = "";
        public long Size;
        public bool? Passed, CriticalWarning;
        public double? Temp, Wear;
        public long? PowerOnHours, PowerCycles, MediaErrors, Reallocated, Pending, Uncorrectable;
        public double? WrittenGb, ReadGb;
        public string? Problem;
    }

    public List<DiskHealthCard> Read(IEnumerable<SensorRow>? sensors = null)
    {
        var disks = LinuxSystemInfo.Disks();
        if (disks.Count == 0)
            return new List<DiskHealthCard> { new() { Title = "Nenhum disco encontrado", Verdict = DiskVerdict.Unknown, Reasons = "O lsblk e o /sys/block não listaram discos." } };

        bool hasSmartctl = Sys.HasCommand("smartctl");
        var raws = new List<Raw>();
        for (int i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            var r = new Raw
            {
                Index = i, Model = d.Model.Length > 0 ? d.Model : d.Name, Serial = d.Serial, Firmware = d.Rev, Size = d.Size,
                Bus = LinuxSystemInfo.Bus(d), Media = d.Rota ? "HDD" : "SSD", Device = "/dev/" + d.Name,
            };
            if (!hasSmartctl) r.Problem = "Instale o smartmontools (sudo apt install smartmontools, sudo dnf install smartmontools ou sudo pacman -S smartmontools) para ler a saúde.";
            else if (!Sys.IsRoot) r.Problem = "O S.M.A.R.T. só pode ser lido como root. Abra o ABExplode com sudo (ou pkexec) para ver a saúde completa.";
            else ReadSmart(r);
            raws.Add(r);
        }

        // Sensores do hwmon (o modelo do disco é o nome do hardware): temperatura sem precisar de root.
        if (sensors != null)
        {
            var list = sensors.Where(x => x.HardwareType == HardwareKind.Storage && x.Type == SensorKind.Temperature && x.RawValue is > 0).ToList();
            foreach (var r in raws.Where(r => r.Temp == null))
                r.Temp = list.FirstOrDefault(x => x.Hardware.Contains(r.Model, StringComparison.OrdinalIgnoreCase) ||
                                                  r.Model.Contains(x.Hardware, StringComparison.OrdinalIgnoreCase))?.RawValue;
        }
        return raws.Select(ToCard).ToList();
    }

    private static void ReadSmart(Raw r)
    {
        // Saída != 0 também quando há aviso no disco (bits do código de saída): o JSON vem mesmo assim.
        string? json = Sys.Run("smartctl", $"-j -a {r.Device}", 15000, acceptNonZero: true);
        if (json == null) { r.Problem = "O smartctl não respondeu."; return; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement? P(JsonElement e, params string[] path)
            {
                foreach (var k in path)
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out var next)) return null;
                    e = next;
                }
                return e;
            }
            long? N(params string[] path) => P(root, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out long x) ? x : null;

            if (P(root, "smart_status", "passed") is { } passed && passed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                r.Passed = passed.GetBoolean();
            if (N("temperature", "current") is long t && t > 0) r.Temp = t;
            r.PowerOnHours = N("power_on_time", "hours");
            r.PowerCycles = N("power_cycle_count");
            if (P(root, "rotation_rate") is { ValueKind: JsonValueKind.Number } rot) r.Media = rot.GetInt32() > 0 ? "HDD" : "SSD";
            if (P(root, "firmware_version") is { ValueKind: JsonValueKind.String } fw) r.Firmware = fw.GetString() ?? r.Firmware;

            // NVMe
            if (P(root, "nvme_smart_health_information_log") is { ValueKind: JsonValueKind.Object } nv)
            {
                long? NV(string k) => nv.TryGetProperty(k, out var v) && v.TryGetInt64(out long x) ? x : null;
                r.Wear = NV("percentage_used");
                r.MediaErrors = NV("media_errors");
                r.CriticalWarning = NV("critical_warning") is long cw ? cw != 0 : null;
                // data units = 1000 blocos de 512 bytes
                if (NV("data_units_written") is long w) r.WrittenGb = w * 512000.0 / 1e9;
                if (NV("data_units_read") is long rd) r.ReadGb = rd * 512000.0 / 1e9;
            }

            // SATA/ATA: tabela de atributos
            if (P(root, "ata_smart_attributes", "table") is { ValueKind: JsonValueKind.Array } table)
                foreach (var a in table.EnumerateArray())
                {
                    int id = a.TryGetProperty("id", out var idv) ? idv.GetInt32() : 0;
                    long raw = a.TryGetProperty("raw", out var rv) && rv.TryGetProperty("value", out var rvv) && rvv.TryGetInt64(out long x) ? x : 0;
                    int value = a.TryGetProperty("value", out var vv) ? vv.GetInt32() : 0;
                    switch (id)
                    {
                        case 5: r.Reallocated = raw; break;
                        case 197: r.Pending = raw; break;
                        case 198: r.Uncorrectable = raw; break;
                        case 177 or 231 or 233 when r.Wear == null && value is > 0 and <= 100: r.Wear = 100 - value; break; // vida restante (normalizado)
                        case 241: r.WrittenGb ??= raw * 512.0 / 1e9; break; // LBAs gravados
                        case 242: r.ReadGb ??= raw * 512.0 / 1e9; break;
                    }
                }
            if (P(root, "smartctl", "messages") is { ValueKind: JsonValueKind.Array } msgs && r.Passed == null)
                r.Problem = string.Join(" ", msgs.EnumerateArray().Select(m => m.TryGetProperty("string", out var s) ? s.GetString() : null).Where(s => s != null));
        }
        catch (Exception ex) { r.Problem = "Não foi possível ler a saída do smartctl: " + ex.Message; }
    }

    private static DiskHealthCard ToCard(Raw r)
    {
        bool isSsd = r.Media == "SSD" || r.Bus == "NVMe";
        var crit = new List<string>();
        var warn = new List<string>();
        bool anyData = r.Passed != null || r.Wear != null || r.Temp != null || r.PowerOnHours != null;

        if (r.Passed == false) crit.Add("O próprio disco avisa (S.M.A.R.T.) que deve falhar em breve. Faça backup agora.");
        if (r.CriticalWarning == true) crit.Add("O SSD NVMe registrou um aviso crítico (falta de reserva, temperatura ou modo somente leitura).");
        if (r.Wear is double w)
        {
            if (w >= 95) crit.Add($"Desgaste de {w:0}%: o SSD está no fim da vida útil. Troque e faça backup.");
            else if (w >= 80) warn.Add($"Desgaste de {w:0}%: o SSD já gastou boa parte da vida útil. Planeje a troca.");
        }
        if (r.Temp is double t)
        {
            double okMax = isSsd ? 70 : 55, critMax = isSsd ? 85 : 65;
            if (t >= critMax) crit.Add($"Temperatura de {t:0} °C é muito alta para este disco.");
            else if (t >= okMax) warn.Add($"Temperatura de {t:0} °C está alta. Melhore a ventilação.");
        }
        if (r.Reallocated > 0) warn.Add($"{r.Reallocated} setores realocados: o disco já trocou áreas defeituosas. Acompanhe e mantenha backup.");
        if (r.Pending > 0 || r.Uncorrectable > 0) crit.Add($"{(r.Pending ?? 0) + (r.Uncorrectable ?? 0)} setores com erro de leitura que não foram corrigidos.");
        if (r.MediaErrors > 0) warn.Add($"{r.MediaErrors} erros de mídia registrados pelo SSD.");

        DiskVerdict v = crit.Count > 0 ? DiskVerdict.Critical : warn.Count > 0 ? DiskVerdict.Warning : anyData ? DiskVerdict.Good : DiskVerdict.Unknown;
        var reasons = crit.Concat(warn).ToList();
        if (reasons.Count == 0)
            reasons.Add(r.Problem ?? (v == DiskVerdict.Good
                ? "Nenhum problema encontrado nas leituras disponíveis."
                : "Este disco não informa dados de saúde (comum em pendrives e alguns HDs externos)."));
        else if (r.Problem != null) reasons.Add(r.Problem);

        var meta = new[] { r.Media, r.Bus, FmtSize(r.Size), r.Device }.Where(x => !string.IsNullOrEmpty(x) && x != "—");
        var metrics = new List<KeyValuePair<string, string>>();
        void M(string k, string? val) { if (val != null) metrics.Add(new(k, val)); }
        M("Temperatura", r.Temp is double tt ? $"{tt:0} °C" : null);
        if (r.Wear is double wr) { M("Desgaste", $"{wr:0}%"); M("Vida útil restante", $"{Math.Max(0, 100 - wr):0}%"); }
        M("Horas ligado", r.PowerOnHours is long h ? $"{h:N0} h ({h / 24.0:N0} dias)" : null);
        M("Ciclos liga/desliga", r.PowerCycles is long pc ? $"{pc:N0}" : null);
        M("Setores realocados", r.Reallocated is long re ? $"{re:N0}" : null);
        M("Setores pendentes", r.Pending is long pe ? $"{pe:N0}" : null);
        M("Erros de mídia", r.MediaErrors is long me ? $"{me:N0}" : null);
        M("Total gravado", r.WrittenGb is double wg && wg > 0 ? $"{wg:N0} GB" : null);
        M("Total lido", r.ReadGb is double rg && rg > 0 ? $"{rg:N0} GB" : null);
        M("Teste geral do S.M.A.R.T.", r.Passed is bool ok ? (ok ? "Aprovado" : "REPROVADO") : null);
        M("Firmware", r.Firmware.Length > 0 ? r.Firmware : null);

        return new DiskHealthCard
        {
            Index = r.Index, Title = r.Model, Meta = string.Join(" · ", meta), Serial = r.Serial,
            Verdict = r.Bus == "USB" && !anyData ? DiskVerdict.Unknown : v,
            Reasons = string.Join(" ", reasons), Metrics = metrics,
        };
    }

    private static string FmtSize(long b)
    {
        double v = b; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return b > 0 ? $"{v:0.#} {u[i]}" : "";
    }
}
