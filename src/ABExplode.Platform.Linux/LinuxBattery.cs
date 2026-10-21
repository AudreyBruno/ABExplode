namespace ABExplode.Platform.Linux;

/// <summary>
/// Bateria (desgaste, ciclos, carga) e energia (fonte, perfil, consumo dos sensores) por /sys/class/power_supply,
/// /sys/firmware/acpi/platform_profile e powerprofilesctl. Nada é alterado.
/// </summary>
internal static class LinuxBattery
{
    public static List<DiskHealthCard> Read(IEnumerable<SensorRow>? sensors = null)
    {
        var cards = new List<DiskHealthCard>();
        var supplies = Sys.Dirs("/sys/class/power_supply").ToList();
        var bats = supplies.Where(s => Sys.Read(Path.Combine(s, "type")) == "Battery" && Sys.Read(Path.Combine(s, "scope")) != "Device").ToList();
        bool onAc = supplies.Any(s => Sys.Read(Path.Combine(s, "type")) is "Mains" or "USB" && Sys.Read(Path.Combine(s, "online")) == "1");

        for (int i = 0; i < bats.Count; i++)
        {
            string b = bats[i];
            long? L(string f) => Sys.ReadLong(Path.Combine(b, f));
            string? S(string f) => Sys.Read(Path.Combine(b, f)) is string v && v.Length > 0 ? v : null;

            // energy_* em µWh; alguns notebooks só têm charge_* (µAh), que vira Wh pela tensão de projeto.
            double? volt = L("voltage_min_design") is long vmd && vmd > 0 ? vmd / 1e6 : L("voltage_now") is long vn && vn > 0 ? vn / 1e6 : null;
            double? Wh(string energy, string charge) =>
                L(energy) is long e && e > 0 ? e / 1e6 : L(charge) is long c && c > 0 && volt is double v ? c / 1e6 * v : null;
            double? design = Wh("energy_full_design", "charge_full_design");
            double? full = Wh("energy_full", "charge_full");
            long? cycles = L("cycle_count");
            if (cycles == 0) cycles = null;
            long? pct = L("capacity");
            string status = S("status") ?? "";
            if (status is "Charging" or "Full" or "Not charging") onAc = true;

            double? wear = design > 0 && full > 0 ? Math.Max(0, 100.0 * (1 - full.Value / design.Value)) : null;
            var reasons = new List<string>();
            var verdict = DiskVerdict.Good;
            string label = "SAUDÁVEL";
            if (wear == null) { verdict = DiskVerdict.Unknown; label = "SEM DADOS"; reasons.Add("O sistema não informou a capacidade de projeto ou a atual, então o desgaste não pôde ser calculado."); }
            else if (wear >= 50) { verdict = DiskVerdict.Critical; label = "DESGASTADA"; reasons.Add($"Perdeu {wear:0}% da capacidade original: a autonomia é metade do que era. Vale trocar a bateria."); }
            else if (wear >= 30) { verdict = DiskVerdict.Warning; label = "ATENÇÃO"; reasons.Add($"Perdeu {wear:0}% da capacidade original. Ainda funciona, mas a autonomia já caiu bastante."); }
            else if (wear >= 15) reasons.Add($"Desgaste de {wear:0}%, normal para uma bateria com uso.");
            else reasons.Add($"Desgaste de apenas {wear:0}%: bateria em ótimo estado.");
            if (cycles is >= 1000 && verdict == DiskVerdict.Good) { verdict = DiskVerdict.Warning; label = "ATENÇÃO"; }
            if (cycles is >= 800) reasons.Add($"{cycles} ciclos de carga: perto do fim da vida útil típica das baterias de íon-lítio (500 a 1000).");
            if (pct is < 20 && !onAc) reasons.Add("Carga baixa e fora da tomada.");
            if (S("charge_control_end_threshold") is string limit && limit != "100")
                reasons.Add($"A carga está limitada a {limit}% para poupar a bateria.");

            var m = new List<KeyValuePair<string, string>>();
            void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) m.Add(new(k, v)); }
            Add("Carga atual", pct != null ? $"{pct}%" : null);
            Add("Estado", status switch
            {
                "Charging" => "Carregando", "Discharging" => "Descarregando", "Full" => "Totalmente carregada",
                "Not charging" => "Na tomada (sem carregar)", "" => null, _ => status
            });
            double? powerW = L("power_now") is long pn && pn > 0 ? pn / 1e6 : L("current_now") is long cn && cn > 0 && L("voltage_now") is long vv ? cn / 1e6 * (vv / 1e6) : null;
            double? nowWh = Wh("energy_now", "charge_now");
            if (status == "Discharging" && powerW > 0.5 && nowWh > 0)
            {
                double h = nowWh.Value / powerW.Value;
                Add("Autonomia estimada", $"{(int)h}h {(int)(h % 1 * 60):00}min");
            }
            Add("Capacidade de projeto", design != null ? $"{design:0.0} Wh" : null);
            Add("Capacidade máxima atual", full != null ? $"{full:0.0} Wh" : null);
            Add("Desgaste", wear != null ? $"{wear:0.0}%" : null);
            Add("Ciclos de carga", cycles?.ToString());
            if (L("voltage_now") is long vnow && vnow > 0) Add("Tensão", $"{vnow / 1e6:0.00} V");
            if (powerW > 0) Add(status == "Charging" ? "Potência de carga" : "Consumo atual", $"{powerW:0.0} W");
            Add("Fabricante", S("manufacturer"));
            Add("Modelo", S("model_name"));
            Add("Química", S("technology") switch { "Li-ion" => "Íon-lítio", "Li-poly" => "Polímero de lítio", "NiMH" => "NiMH", "Unknown" or null => null, var t => t });

            cards.Add(new DiskHealthCard
            {
                Title = bats.Count > 1 ? $"Bateria {i + 1}" : "Bateria",
                Meta = (S("manufacturer") ?? "") + (pct != null ? $" · {pct}% de carga" : ""),
                Verdict = verdict, Label = label,
                Reasons = string.Join(" ", reasons),
                Metrics = m,
            });
        }

        // Cartão de energia (existe em qualquer PC)
        string? profile = Sys.Run("powerprofilesctl", "get")?.Trim();
        if (string.IsNullOrEmpty(profile)) profile = Sys.Read("/sys/firmware/acpi/platform_profile");
        string? governor = Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor");
        string profileName = profile switch
        {
            "performance" => "Desempenho", "balanced" => "Equilibrado", "power-saver" or "low-power" or "quiet" => "Economia de energia",
            "balanced-performance" => "Equilibrado (desempenho)", null or "" => "", var p => p,
        };
        var em = new List<KeyValuePair<string, string>>
        {
            new("Fonte de energia", bats.Count == 0 ? "Tomada (PC de mesa)" : onAc ? "Tomada" : "Bateria"),
        };
        if (profileName.Length > 0) em.Add(new("Perfil de energia", profileName));
        if (governor != null) em.Add(new("Governador da CPU", governor));
        if (sensors != null)
            foreach (var s in sensors.Where(x => x.Type == SensorKind.Power && x.RawValue.GetValueOrDefault() > 0).Take(6))
                em.Add(new($"{s.Hardware} · {s.Name}", s.Value));
        bool slow = profile is "power-saver" or "low-power" or "quiet" || governor is "powersave" && profile is null or "";
        string advice = slow ? " Para jogos e benchmarks, use o perfil Desempenho (powerprofilesctl set performance) para o PC não diminuir o clock." : "";
        bool hasGameMode = Sys.HasCommand("gamemoded") || Sys.HasCommand("gamemoderun");
        cards.Add(new DiskHealthCard
        {
            Title = "Energia",
            Meta = bats.Count == 0 ? "Nenhuma bateria detectada" : "Fonte, perfil e consumo",
            Verdict = DiskVerdict.Good, Label = "EM USO",
            Reasons = (profileName.Length > 0 ? $"Perfil de energia ativo: {profileName}." : "Perfil de energia não identificado (sem power-profiles-daemon).") + advice
                      + (hasGameMode ? " GameMode instalado: rode os jogos com gamemoderun %command%." : "")
                      + (bats.Count == 0 ? " O consumo da fonte (PSU) não é medido pelo sistema; os valores abaixo são dos sensores de CPU e GPU." : ""),
            Metrics = em,
        });
        return cards;
    }
}
