using AuditPlatform.Domain.Templates;

namespace AuditPlatform.Domain.Tests;

public class AupMarkdownParserTests
{
    // Excerpt in the format of tubeoism/ia-aup-procedures (IA_AUP_Standard_En/1. cash_management_process.md).
    private const string Sample = """
        # Audit Area: Cash Management

        ## I. Audit Objectives

        *   Ensure that cash and cash equivalents are real, owned by the Company, and recorded fully and accurately.
        *   Evaluate the effectiveness of the internal control system regarding the cash management process.

        ## II. Detailed Audit Procedures

        ### 1. AUP-CSH-01: Reconciliation of Aggregated Data

        - **Procedure:** Reconcile cash balances on the Financial Statements with the General Ledger and Sub-ledgers.
        - **Audit Content:**
            - Compare the balances of cash on hand and bank deposits on the Balance Sheet with the General Ledger (GL).
            - Reconcile total receipts/payments on the GL with the Sub-ledgers.
        - **Evidence to Check:**
            - Balance Sheet.
            - Trial Balance.

        ### 3. AUP-CSH-03: Cash Count

        - **Procedure:** Perform a surprise cash count at the time of the audit.
        - **Audit Content:**
            - Physically count all cash, foreign currency, and valuable documents currently kept in the safe.
        - **Evidence to Check:**
            - Cash Count Report.
        """;

    [Fact]
    public void Parses_area_objectives_and_procedures()
    {
        var area = AupMarkdownParser.Parse(Sample, "en");

        Assert.Equal("Cash Management", area.AreaName);
        Assert.Equal(2, area.Objectives.Count);
        Assert.Equal(["AUP-CSH-01", "AUP-CSH-03"], area.Procedures.Select(p => p.Code));

        var first = area.Procedures[0];
        Assert.Equal("CSH", first.AreaCode);
        Assert.Equal("Reconciliation of Aggregated Data", first.Title);
        Assert.StartsWith("Reconcile cash balances", first.Procedure, StringComparison.Ordinal);
        Assert.Equal(2, first.AuditContent.Count);
        Assert.Equal(["Balance Sheet.", "Trial Balance."], first.EvidenceToCheck);
        Assert.Equal("en", first.Language);
    }

    [Fact]
    public void Section_labels_are_matched_by_position_so_translated_files_parse()
    {
        const string vi = """
            # Khu vực kiểm toán: Quản lý tiền

            ## I. Mục tiêu kiểm toán

            *   Đảm bảo tiền và tương đương tiền là có thật.

            ## II. Thủ tục kiểm toán chi tiết

            ### 1. AUP-CSH-01: Đối chiếu số liệu tổng hợp

            - **Thủ tục:** Đối chiếu số dư tiền trên BCTC với Sổ cái.
            - **Nội dung kiểm tra:**
                - So sánh số dư tiền mặt.
            - **Bằng chứng cần kiểm tra:**
                - Bảng cân đối kế toán.
            """;

        var area = AupMarkdownParser.Parse(vi, "vi");
        var p = Assert.Single(area.Procedures);
        Assert.Equal("Quản lý tiền", area.AreaName);
        Assert.Equal("Đối chiếu số liệu tổng hợp", p.Title);
        Assert.Single(p.AuditContent);
        Assert.Equal(["Bảng cân đối kế toán."], p.EvidenceToCheck);
    }
}
