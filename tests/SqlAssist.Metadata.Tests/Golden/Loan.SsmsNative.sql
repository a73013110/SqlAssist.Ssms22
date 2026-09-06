SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Loan](
	[LoanId] [int] IDENTITY(1,1) NOT NULL,
	[PublicId] [uniqueidentifier] NOT NULL CONSTRAINT [DF_Loan_PublicId] DEFAULT (newsequentialid()),
	[Status] [tinyint] NOT NULL,
	[Title] [nvarchar](200) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
	[Remark] [nvarchar](max) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
	[BranchNo] [varchar](50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
	[LoanUser] [varchar](50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
	[LoanTime] [datetime] NOT NULL,
	[DueTime] [datetime] NULL,
	[TargetBranchNo] [varchar](50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
	[RenewCount] [int] NOT NULL CONSTRAINT [DF_Loan_RenewCount] DEFAULT ((0)),
	[IsActive] [bit] NOT NULL CONSTRAINT [DF_Loan_IsActive] DEFAULT ((1)),
	[CreateUser] [nvarchar](50) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL,
	[CreateTime] [datetime] NOT NULL CONSTRAINT [DF_Loan_CreateTime] DEFAULT (getdate()),
	[UpdateUser] [nvarchar](50) COLLATE Chinese_Taiwan_Stroke_CI_AS NULL,
	[UpdateTime] [datetime] NULL,
	CONSTRAINT [PK_Loan] PRIMARY KEY CLUSTERED ([LoanId] ASC)
)
GO
CREATE NONCLUSTERED INDEX [IX_Loan_1] ON [dbo].[Loan] ([Status] ASC, [LoanTime] DESC) INCLUDE ([IsActive], [DueTime])
GO
CREATE UNIQUE NONCLUSTERED INDEX [IX_Loan_2] ON [dbo].[Loan] ([PublicId] ASC)
GO
CREATE NONCLUSTERED INDEX [IX_Loan_3] ON [dbo].[Loan] ([TargetBranchNo] ASC, [LoanTime] ASC) INCLUDE ([LoanId], [PublicId], [Status], [Title], [Remark], [BranchNo], [LoanUser], [DueTime], [RenewCount]) WHERE ([IsActive]=(1))
GO
EXEC sp_addextendedproperty N'MS_Description', N'借閱主表', 'SCHEMA', N'dbo', 'TABLE', N'Loan', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', N'借閱編號（自動遞增）', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'LoanId'
GO
EXEC sp_addextendedproperty N'MS_Description', N'借閱 UUID（唯一），提供公開查詢', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'PublicId'
GO
EXEC sp_addextendedproperty N'MS_Description', N'狀態：1=預約, 2=借出, 3=歸還', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'Status'
GO
EXEC sp_addextendedproperty N'MS_Description', N'書名', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'Title'
GO
EXEC sp_addextendedproperty N'MS_Description', N'備註', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'Remark'
GO
EXEC sp_addextendedproperty N'MS_Description', N'借出分館', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'BranchNo'
GO
EXEC sp_addextendedproperty N'MS_Description', N'借閱人員（Reader''s account）', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'LoanUser'
GO
EXEC sp_addextendedproperty N'MS_Description', N'借出時間', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'LoanTime'
GO
EXEC sp_addextendedproperty N'MS_Description', N'到期時間（可為 null，表示不限期）', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'DueTime'
GO
EXEC sp_addextendedproperty N'MS_Description', N'還書分館', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'TargetBranchNo'
GO
EXEC sp_addextendedproperty N'MS_Description', N'續借次數', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'RenewCount'
GO
EXEC sp_addextendedproperty N'MS_Description', N'啟用狀態', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'IsActive'
GO
EXEC sp_addextendedproperty N'MS_Description', N'建立者', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'CreateUser'
GO
EXEC sp_addextendedproperty N'MS_Description', N'建立時間', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'CreateTime'
GO
EXEC sp_addextendedproperty N'MS_Description', N'修改者', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'UpdateUser'
GO
EXEC sp_addextendedproperty N'MS_Description', N'修改時間', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'COLUMN', N'UpdateTime'
GO
EXEC sp_addextendedproperty N'MS_Description', N'公開查詢用的涵蓋索引', 'SCHEMA', N'dbo', 'TABLE', N'Loan', 'INDEX', N'IX_Loan_3'
GO
