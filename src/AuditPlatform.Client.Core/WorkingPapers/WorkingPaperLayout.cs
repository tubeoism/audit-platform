using System.Globalization;
using AuditPlatform.Domain.Templates;

namespace AuditPlatform.Client.Core.WorkingPapers;

public enum FieldSection
{
    /// <summary>One block per audit-content step of the procedure.</summary>
    Steps = 0,
    Scope = 1,
    Results = 2,
}

/// <param name="Key">Field key in the synchronised document. Never changes once working papers exist.</param>
/// <param name="Label">What the form shows above the box.</param>
public sealed record FieldSpec(string Key, FieldSection Section, string Label);

/// <summary>
/// The form of a working paper, derived from its procedure template: one "work performed" box per audit-content
/// step, then scope (population, sample) and results (exceptions, factual findings). AUP engagements report factual
/// findings, not an assurance conclusion, so there is no conclusion field.
/// Field keys are part of the signed content: add keys freely, never rename or reuse one.
/// </summary>
public static class WorkingPaperLayout
{
    public const string Population = "scope.population";
    public const string Sample = "scope.sample";
    public const string Exceptions = "result.exceptions";
    public const string Findings = "result.findings";

    public static string StepKey(int number) => string.Create(CultureInfo.InvariantCulture, $"step.{number:00}.work");

    public static IReadOnlyList<FieldSpec> For(ProcedureTemplate? template, string language)
    {
        var vi = language != "en";
        var fields = new List<FieldSpec>();
        var steps = template?.AuditContent ?? [];
        for (var i = 0; i < steps.Count; i++)
        {
            fields.Add(new FieldSpec(StepKey(i + 1), FieldSection.Steps, string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {steps[i]}")));
        }

        fields.Add(new FieldSpec(Population, FieldSection.Scope, vi ? "Tổng thể và nguồn dữ liệu" : "Population and data source"));
        fields.Add(new FieldSpec(Sample, FieldSection.Scope, vi ? "Mẫu chọn và phương pháp chọn mẫu" : "Sample selected and selection method"));
        fields.Add(new FieldSpec(Exceptions, FieldSection.Results, vi ? "Ngoại lệ ghi nhận" : "Exceptions noted"));
        fields.Add(new FieldSpec(Findings, FieldSection.Results, vi ? "Kết quả thực tế (factual findings)" : "Factual findings"));
        return fields;
    }

    /// <summary>A label for a key the current layout does not know, so content saved under an older layout is never hidden.</summary>
    public static FieldSpec Unknown(string key) => new(key, FieldSection.Results, key);
}
