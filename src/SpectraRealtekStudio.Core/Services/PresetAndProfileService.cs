using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

public sealed class PresetAndProfileService
{
    private readonly string _appDataFolder;
    private readonly string _customPresetsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PresetAndProfileService()
    {
        _appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpectraRealtekStudio");
        _customPresetsFilePath = Path.Combine(_appDataFolder, "custom_eq_presets.json");
    }

    public List<EqPresetDefinition> GetAllEqPresets()
    {
        var all = new List<EqPresetDefinition>(RealtekPropertyKeys.BuiltInEqPresets);
        all.AddRange(LoadCustomEqPresets());
        return all;
    }

    public List<EqPresetDefinition> LoadCustomEqPresets()
    {
        try
        {
            if (!File.Exists(_customPresetsFilePath))
                return new List<EqPresetDefinition>();

            string json = File.ReadAllText(_customPresetsFilePath);
            var loaded = JsonSerializer.Deserialize<List<EqPresetDefinition>>(json, JsonOptions);
            return loaded ?? new List<EqPresetDefinition>();
        }
        catch
        {
            return new List<EqPresetDefinition>();
        }
    }

    public void SaveCustomEqPreset(string name, double[] bandsDb)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Preset name cannot be empty.", nameof(name));

        double[] copy = new double[10];
        for (int i = 0; i < 10; i++)
        {
            copy[i] = i < bandsDb.Length ? Math.Round(Math.Clamp(bandsDb[i], -12.0, 12.0), 1) : 0.0;
        }

        var customs = LoadCustomEqPresets();
        int existingIdx = customs.FindIndex(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        var def = new EqPresetDefinition(name.Trim(), false, copy);
        if (existingIdx >= 0)
            customs[existingIdx] = def;
        else
            customs.Add(def);

        Directory.CreateDirectory(_appDataFolder);
        File.WriteAllText(_customPresetsFilePath, JsonSerializer.Serialize(customs, JsonOptions));
    }

    public bool DeleteCustomEqPreset(string name)
    {
        var customs = LoadCustomEqPresets();
        int removed = customs.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Directory.CreateDirectory(_appDataFolder);
            File.WriteAllText(_customPresetsFilePath, JsonSerializer.Serialize(customs, JsonOptions));
            return true;
        }
        return false;
    }

    public string MatchPresetName(double[] bandsDb)
    {
        if (bandsDb == null || bandsDb.Length < 10)
            return "Custom";

        foreach (var preset in GetAllEqPresets())
        {
            bool matches = true;
            for (int i = 0; i < 10; i++)
            {
                if (Math.Abs(preset.BandsDb[i] - bandsDb[i]) > 0.09)
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
                return preset.Name;
        }
        return "Custom";
    }

    public void ExportProfileToFile(string filePath, StudioProfileExport profile)
    {
        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public StudioProfileExport? ImportProfileFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<StudioProfileExport>(json, JsonOptions);
    }
}
