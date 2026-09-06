CREATE TABLE [dbo].[Loan]
(
[LoanId] [int] NOT NULL IDENTITY(1, 1),
[PublicId] [uniqueidentifier] NOT NULL CONSTRAINT [DF_Loan_PublicId] DEFAULT (newsequentialid()),
[Status] [tinyint] NOT NULL,
[Title] [nvarchar] (200) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
[Remark] [nvarchar] (max) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
[BranchNo] [varchar] (50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
[LoanUser] [varchar] (50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
[LoanTime] [datetime] NOT NULL,
[DueTime] [datetime] NULL,
[TargetBranchNo] [varchar] (50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
[RenewCount] [int] NOT NULL CONSTRAINT [DF_Loan_RenewCount] DEFAULT ((0)),
[IsActive] [bit] NOT NULL CONSTRAINT [DF_Loan_IsActive] DEFAULT ((1)),
[CreateUser] [nvarchar] (50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
[CreateTime] [datetime] NOT NULL CONSTRAINT [DF_Loan_CreateTime] DEFAULT (getdate()),
[UpdateUser] [nvarchar] (50) COLLATE Chinese_Taiwan_Stroke_CI_AS NULL,
[UpdateTime] [datetime] NULL
)
GO
ALTER TABLE [dbo].[Loan] ADD CONSTRAINT [PK_Loan] PRIMARY KEY CLUSTERED ([LoanId])
GO
CREATE NONCLUSTERED INDEX [IX_Loan_1] ON [dbo].[Loan] ([Status], [LoanTime] DESC) INCLUDE ([IsActive], [DueTime])
GO
CREATE UNIQUE NONCLUSTERED INDEX [IX_Loan_2] ON [dbo].[Loan] ([PublicId])
GO
CREATE NONCLUSTERED INDEX [IX_Loan_3] ON [dbo].[Loan] ([TargetBranchNo], [LoanTime]) INCLUDE ([LoanId], [PublicId], [Status], [Title], [Remark], [BranchNo], [LoanUser], [DueTime], [RenewCount]) WHERE ([IsActive]=(1))
GO
