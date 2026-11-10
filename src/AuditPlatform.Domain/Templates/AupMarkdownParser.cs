using System.Text.RegularExpressions;

namespace AuditPlatform.Domain.Templates;

/// <summary>
/// Parses the methodology Markdown used in the ia-aup-procedures repository:
/// <code>
/// # Audit Area: Cash Management
/// ## I. Audit Objectives
/// *   objective ...
/// ## II. Detailed Audit Procedures
/// ### 1. AUP-CSH-01: Reconciliation of Aggregated Data
/// - **Procedure:** ...
/// - **Audit Content:**
///     - step ...
/// - **Evidence to Check:**
///     - item ...
/// </code>
/// The parser is deliberately tolerant of the Vietnamese edition, where the section labels are translated:
/// it keys on the procedure code pattern and on the order of the three bold labels.
/// </summary>
public static partial class AupMarkdownParser
{
    [GeneratedRegex(@"^#\s+(?:[^:]+:\s*)?(?<name>.+?)\s*$")]
    private static partial Regex AreaHeading();

    [GeneratedRegex(@"^###\s+(?:\d+\.\s*)?(?<code>AUP-(?<area>[A-Z]+)-\d+[A-Z]?)\s*[:\-–]\s*(?<title>.+?)\s*$")]
    private static partial Regex ProcedureHeading();

    [GeneratedRegex(@"^-\s+\*\*(?<label>[^*]+?):?\*\*:?\s*(?<rest>.*)$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^\s+[-*]\s+(?<item>.+?)\s*$")]
    private static partial Regex NestedBullet();

    [GeneratedRegex(@"^\*\s+(?<item>.+?)\s*$")]
    private static partial Regex TopBullet();

    public static AuditAreaTemplate Parse(string markdown, string language, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var areaName = string.Empty;
        var objectives = new List<string>();
        var procedures = new List<ProcedureTemplate>();
        var inObjectives = false;
        var level2Count = 0;

        ProcedureBuilder? current = null;
        var section = 0; // 1 = procedure, 2 = audit content, 3 = evidence

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal) && areaName.Length == 0)
            {
                areaName = AreaHeading().Match(line).Groups["name"].Value;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                // The first level-2 section of each methodology file holds the objectives.
                level2Count++;
                inObjectives = level2Count == 1;
                continue;
            }

            var heading = ProcedureHeading().Match(line);
            if (heading.Success)
            {
                Flush();
                inObjectives = false;
                current = new ProcedureBuilder(heading.Groups["code"].Value, heading.Groups["area"].Value, heading.Groups["title"].Value);
                section = 0;
                continue;
            }

            if (inObjectives)
            {
                var bullet = TopBullet().Match(line);
                if (bullet.Success)
                {
                    objectives.Add(bullet.Groups["item"].Value);
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            var label = LabelLine().Match(line);
            if (label.Success)
            {
                section++;
                if (section == 1)
                {
                    current.Procedure = label.Groups["rest"].Value.Trim();
                }

                continue;
            }

            var nested = NestedBullet().Match(line);
            if (nested.Success)
            {
                (section == 3 ? current.Evidence : current.Content).Add(nested.Groups["item"].Value);
            }
        }

        Flush();
        return new AuditAreaTemplate(areaName, objectives, procedures);

        void Flush()
        {
            if (current is not null)
            {
                procedures.Add(new ProcedureTemplate(
                    current.Code, current.AreaCode, areaName, current.Title, current.Procedure,
                    current.Content, current.Evidence, language, version));
                current = null;
            }
        }
    }

    private sealed class ProcedureBuilder(string code, string areaCode, string title)
    {
        public string Code { get; } = code;
        public string AreaCode { get; } = areaCode;
        public string Title { get; } = title;
        public string Procedure { get; set; } = string.Empty;
        public List<string> Content { get; } = [];
        public List<string> Evidence { get; } = [];
    }
}
