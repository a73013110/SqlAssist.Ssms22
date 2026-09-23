// 由 tools/Generate-FeatureDemoData.cs 產生，請勿手改 SQL。
window.featureDemoData = {
  "definition": "SET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\nCREATE TABLE [dbo].[Loan]\n(\n    [LoanId] int NOT NULL,\n    [CopyNo] nvarchar(20) NOT NULL,\n    [ReaderId] int NOT NULL\n)\nGO\nALTER TABLE [dbo].[Loan] ADD CONSTRAINT [PK_Loan] PRIMARY KEY CLUSTERED ([LoanId])\nGO\nCREATE NONCLUSTERED INDEX [IX_Loan_ReaderId] ON [dbo].[Loan] ([ReaderId])\nGO\nALTER TABLE [dbo].[Loan] ADD CONSTRAINT [FK_Loan_Lib_Reader] FOREIGN KEY ([ReaderId]) REFERENCES [dbo].[Lib_Reader] ([ReaderId])\nGO\nEXEC sp_addextendedproperty N\u0027MS_Description\u0027, N\u0027\u5716\u66F8\u9928\u501F\u95B1\u7D00\u9304\u0027, \u0027SCHEMA\u0027, N\u0027dbo\u0027, \u0027TABLE\u0027, N\u0027Loan\u0027, NULL, NULL\nGO\n",
  "definitionCaret": 74,
  "selection": "SELECT ReaderId, ReaderName\nFROM dbo.Lib_Reader;",
  "surround": "IF 1 = 1\nBEGIN\n    SELECT ReaderId, ReaderName\n    FROM dbo.Lib_Reader;\nEND",
  "surroundOffset": 19,
  "surroundLength": 52,
  "snippets": [
    {
      "shortcut": "cp",
      "title": "CREATE PROCEDURE",
      "description": "\u5EFA\u7ACB\u9810\u5B58\u7A0B\u5E8F"
    },
    {
      "shortcut": "be",
      "title": "BEGIN END",
      "description": "BEGIN\uFF0FEND \u5340\u584A"
    },
    {
      "shortcut": "ifb",
      "title": "IF",
      "description": "\u689D\u4EF6\u6210\u7ACB\u6642\u57F7\u884C\u5340\u584A"
    },
    {
      "shortcut": "ife",
      "title": "IF EXISTS",
      "description": "\u8CC7\u6599\u5B58\u5728\u6642\u57F7\u884C"
    },
    {
      "shortcut": "ifne",
      "title": "IF NOT EXISTS",
      "description": "\u8CC7\u6599\u4E0D\u5B58\u5728\u6642\u57F7\u884C"
    },
    {
      "shortcut": "wl",
      "title": "WHILE",
      "description": "WHILE \u8FF4\u5708"
    },
    {
      "shortcut": "tc",
      "title": "TRY CATCH",
      "description": "TRY\uFF0FCATCH \u4F8B\u5916\u8655\u7406"
    },
    {
      "shortcut": "cur",
      "title": "CURSOR",
      "description": "\u5B8C\u6574\u7684\u672C\u6A5F\u5FEB\u901F\u9806\u5411 CURSOR \u6A23\u677F"
    },
    {
      "shortcut": "trn",
      "title": "\u5B89\u5168\u4EA4\u6613\u6A23\u677F",
      "description": "\u542B XACT_ABORT\u3001TRY\uFF0FCATCH \u8207\u56DE\u5FA9\u7684\u4EA4\u6613"
    },
    {
      "shortcut": "trr",
      "title": "\u4EA4\u6613\u8A66\u8DD1",
      "description": "\u5728\u4EA4\u6613\u88E1\u8A66\u8DD1\u5F8C\u56DE\u5FA9\uFF1B\u78BA\u8A8D\u7121\u8AA4\u518D\u6539\u6210 COMMIT"
    },
    {
      "shortcut": "wcte",
      "title": "\u5305\u6210 CTE",
      "description": "\u628A\u67E5\u8A62\u653E\u9032 CTE \u518D\u67E5\u8A62"
    },
    {
      "shortcut": "wdt",
      "title": "\u5305\u6210\u884D\u751F\u8CC7\u6599\u8868",
      "description": "\u628A\u67E5\u8A62\u653E\u9032 FROM \u7684\u5B50\u67E5\u8A62"
    }
  ],
  "insert": "INSERT INTO dbo.Lib_Tag\n(\n    TagName,\n    CreatedAt,\n    Description\n)\nVALUES\n(\n    N\u0027\u0027,     -- TagName - nvarchar(80)\n    DEFAULT, -- CreatedAt - datetime2\n    NULL     -- Description - nvarchar(200)\n)",
  "insertCaret": 85,
  "execute": "DECLARE @LoanCount int;\nEXEC dbo.usp_Loan_Count @ReaderId = 0,                 -- int\n                        @MinLoanId = 0,                -- int\uFF0C\u9078\u64C7\u6027\n                        @LoanCount = @LoanCount OUTPUT -- int",
  "executeCaret": 60,
  "merge": "MERGE INTO dbo.Cat_BookCopy AS target\nUSING dbo.SourceTable AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED AND 1 = 0 THEN\n    UPDATE SET\n        target.BranchId = source.BranchId\nWHEN NOT MATCHED BY TARGET AND 1 = 0 THEN\n    INSERT\n    (\n        CopyNo,\n        BranchId\n    )\n    VALUES\n    (\n        source.CopyNo,\n        source.BranchId\n    );",
  "mergeCaret": 44,
  "alterProcedure": "ALTER PROCEDURE dbo.usp_Loan_Count\n    @ReaderId int,\n    @MinLoanId int = 0,\n    @LoanCount int OUTPUT\nAS\nBEGIN\n    SET NOCOUNT ON;\n    SELECT @LoanCount = COUNT(*)\n    FROM dbo.Loan\n    WHERE ReaderId = @ReaderId AND LoanId \u003E= @MinLoanId;\nEND",
  "alterFunction": "ALTER FUNCTION dbo.fn_LoanCount(@ReaderId int)\nRETURNS int\nAS\nBEGIN\n    DECLARE @LoanCount int;\n    SELECT @LoanCount = COUNT(*)\n    FROM dbo.Loan\n    WHERE ReaderId = @ReaderId;\n    RETURN @LoanCount;\nEND",
  "gridValues": [
    "B001",
    "B002",
    "B002",
    "B003"
  ],
  "predicate": "-- \u7531 SqlAssist \u5F9E\u67E5\u8A62\u7D50\u679C\u7522\u751F\uFF1A1 \u6B04 \u00D7 3 \u5217\uFF08\u9078\u53D6\u7BC4\u570D\uFF09\u3002\r\nCopyNo IN (N\u0027B001\u0027, N\u0027B002\u0027)\r\n"
};
